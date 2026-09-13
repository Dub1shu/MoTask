# gong-wpf-dragdrop の撤去 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** カンバンの D&D を WPF 標準の `System.Windows.DragDrop` による自前実装に置き換え、個人 OSS である gong-wpf-dragdrop への依存を消す。

**Architecture:** gong の `IDropInfo` / `IDragInfo` のうち MoTask が実際に使っている 9 メンバーだけを写した自前の抽象（`IDropContext` / `IDragContext` / `IDropHandler` / `IDragHandler`）を置く。既存の 3 ハンドラと `DropPositionCalculator` の判断ロジックは変えず、インターフェースの付け替えだけで載せ替える。WPF 標準 API との接続は添付ビヘイビア `DragDropBehavior` 1 つに閉じ込め、挿入位置の算出は純粋関数に切り出して単体テストする。

**Tech Stack:** .NET 10 / WPF (net10.0-windows) / xunit 2.9.3 / FluentAssertions 7.2.2

**Spec:** `docs/superpowers/specs/2026-09-12-drop-third-party-oss-deps-design.md`

## Global Constraints

- **利用者から見た D&D の振る舞いを変えない。** 何をどこへ落とせるか、落とした結果どう保存されるかは今のまま。
- **ViewModel 層と Core 層に触らない。** `BoardViewModel` / `ColumnViewModel` / `TaskCardViewModel` / `IBoardService` は変更しない。
- **`DropPositionCalculator` のロジックを変えない。** 注釈の文言のみ直してよい。
- **`CardDropHandler` / `ColumnDropHandler` / `ColumnDragHandler` の判断ロジックを変えない。** 型の付け替えと、不要になったメンバーの削除だけ。
- **ViewModel から D&D の型を見せない。** ハンドラはコードビハインドから挿す今の形を保つ。依存は DragDrop → ViewModels の一方通行。
- **この計画では NSubstitute を外さない。** `DropHandlerTests` は引き続き `IBoardService` などを NSubstitute で差し替える。それは 2 本目の計画（`2026-09-13-motask-drop-nsubstitute.md`）の仕事。
- **`AllowUnsafeBlocks` を有効にしない。** P/Invoke は `[DllImport]` で書く。`[LibraryImport]` は unsafe を要求する（SYSLIB1062 で確認済み）。
- **着手前の基準値**: `dotnet build` が 0 警告 0 エラー、テスト 751 本すべて緑（Core 294 / Data 41 / App 395 / Mcp 21）。各タスクの完了時にこれを下回らないこと。
- **MoTask.exe を起動したままビルドしない。** `src/MoTask.App/bin` の DLL がロックされてビルドが失敗する。
- **コミットメッセージ**は日本語の Conventional Commits。末尾にこのセッションの指示する `Co-Authored-By:` 行を付ける。
- **作業ブランチ**: `master` から `feature/drop-gong-dragdrop` を切って作業する。

---

### Task 1: 挿入位置の算出（純粋関数）

カーソル位置から「どの項目の手前に落ちるか」を決める計算。WPF の制御も UI スレッドも要らないので、最初に単体テストで固める。ここがずれると、画面が見せた場所とは別の場所に保存される。

**Files:**
- Create: `src/MoTask.App/DragDrop/InsertIndexCalculator.cs`
- Test: `tests/MoTask.App.Tests/InsertIndexCalculatorTests.cs`

**Interfaces:**
- Consumes: なし
- Produces: `MoTask.App.DragDrop.InsertIndexCalculator.Calculate(IReadOnlyList<(int Index, Rect Bounds)> containers, Point cursor, Orientation orientation) -> int`

戻り値の意味は gong の `InsertIndex` と同じ「`ItemsSource` 上のこの添字の項目の手前に挿す」。末尾に挿す場合は最後の項目の添字に 1 を足した値。`DropPositionCalculator.ToPosition` と `.Reorder` がこの意味づけを前提にしている。

- [ ] **Step 1: 失敗するテストを書く**

`tests/MoTask.App.Tests/InsertIndexCalculatorTests.cs` を新規作成する。

```csharp
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using MoTask.App.DragDrop;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// カーソルがどの項目の手前にあるかを決める計算。戻り値は gong の InsertIndex と
/// 同じ意味（ItemsSource 上のこの添字の手前に挿す）で、DropPositionCalculator がこれを前提にする。
/// </summary>
public class InsertIndexCalculatorTests
{
    /// <summary>高さ 20 の項目を縦に 3 つ並べたもの。y = 0..20, 20..40, 40..60。</summary>
    private static readonly (int Index, Rect Bounds)[] ThreeVertical =
    {
        (0, new Rect(0, 0, 100, 20)),
        (1, new Rect(0, 20, 100, 20)),
        (2, new Rect(0, 40, 100, 20)),
    };

    /// <summary>幅 50 の項目を横に 3 つ並べたもの。x = 0..50, 50..100, 100..150。</summary>
    private static readonly (int Index, Rect Bounds)[] ThreeHorizontal =
    {
        (0, new Rect(0, 0, 50, 100)),
        (1, new Rect(50, 0, 50, 100)),
        (2, new Rect(100, 0, 50, 100)),
    };

    [Fact]
    public void Empty_ReturnsZero()
    {
        InsertIndexCalculator.Calculate(Array.Empty<(int, Rect)>(), new Point(10, 10), Orientation.Vertical)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(5, 0)]    // 1件目の上半分 → 1件目の手前
    [InlineData(15, 1)]   // 1件目の下半分 → 2件目の手前
    [InlineData(25, 1)]   // 2件目の上半分 → 2件目の手前
    [InlineData(35, 2)]   // 2件目の下半分 → 3件目の手前
    [InlineData(45, 2)]   // 3件目の上半分 → 3件目の手前
    [InlineData(55, 3)]   // 3件目の下半分 → 末尾
    [InlineData(500, 3)]  // 全部より下 → 末尾
    public void Vertical_SplitsAtEachMiddle(double y, int expected)
    {
        InsertIndexCalculator.Calculate(ThreeVertical, new Point(50, y), Orientation.Vertical)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(40, 1)]
    [InlineData(60, 1)]
    [InlineData(90, 2)]
    [InlineData(140, 3)]
    public void Horizontal_SplitsAtEachMiddle(double x, int expected)
    {
        InsertIndexCalculator.Calculate(ThreeHorizontal, new Point(x, 50), Orientation.Horizontal)
            .Should().Be(expected);
    }

    /// <summary>
    /// ListBox は項目を仮想化するので、画面外の項目のコンテナは得られない。
    /// 実体化済みのものだけを本来の添字つきで渡す。末尾判定は「最後の実体化済み + 1」。
    /// </summary>
    [Theory]
    [InlineData(105, 5)]
    [InlineData(115, 6)]
    [InlineData(145, 7)]
    [InlineData(155, 8)]
    public void Virtualized_UsesTheRealIndexes(double y, int expected)
    {
        (int, Rect)[] realized =
        {
            (5, new Rect(0, 100, 100, 20)),
            (6, new Rect(0, 120, 100, 20)),
            (7, new Rect(0, 140, 100, 20)),
        };

        InsertIndexCalculator.Calculate(realized, new Point(50, y), Orientation.Vertical).Should().Be(expected);
    }

    /// <summary>項目が 1 つだけのとき、上半分と下半分で 0 と 1 に割れる。</summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(16, 1)]
    public void SingleItem_SplitsInHalf(double y, int expected)
    {
        (int, Rect)[] one = { (0, new Rect(0, 0, 100, 20)) };

        InsertIndexCalculator.Calculate(one, new Point(50, y), Orientation.Vertical).Should().Be(expected);
    }
}
```

- [ ] **Step 2: テストが失敗することを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~InsertIndexCalculatorTests
```

Expected: コンパイルエラー。`InsertIndexCalculator` という名前が存在しない。

- [ ] **Step 3: 最小限の実装を書く**

`src/MoTask.App/DragDrop/InsertIndexCalculator.cs` を新規作成する。

```csharp
using System.Windows;
using System.Windows.Controls;

namespace MoTask.App.DragDrop;

/// <summary>
/// 実体化済みの項目コンテナの位置から「カーソルはどの項目の手前か」を決める純粋な計算。
/// UI スレッドも WPF の制御も要らないので、そのまま単体テストできる。
/// </summary>
public static class InsertIndexCalculator
{
    /// <param name="containers">
    /// 実体化済みコンテナの (ItemsSource 上の添字, ドロップ先要素から見た矩形)。添字の昇順であること。
    /// ListBox は項目を仮想化するので、画面外の項目はここに現れない。
    /// </param>
    /// <param name="cursor">ドロップ先要素から見たカーソル位置。</param>
    /// <returns>
    /// この添字の項目の手前に挿す、という意味の値。末尾なら最後の項目の添字 + 1。
    /// gong の InsertIndex と同じ意味づけで、DropPositionCalculator がこれを前提にする。
    /// </returns>
    public static int Calculate(
        IReadOnlyList<(int Index, Rect Bounds)> containers,
        Point cursor,
        Orientation orientation)
    {
        if (containers.Count == 0) return 0;

        var position = orientation == Orientation.Vertical ? cursor.Y : cursor.X;
        foreach (var (index, bounds) in containers)
        {
            var middle = orientation == Orientation.Vertical
                ? bounds.Top + bounds.Height / 2
                : bounds.Left + bounds.Width / 2;
            if (position < middle) return index;
        }

        return containers[^1].Index + 1;
    }
}
```

- [ ] **Step 4: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter FullyQualifiedName~InsertIndexCalculatorTests
```

Expected: PASS（19 件）

- [ ] **Step 5: コミットする**

```bash
git add src/MoTask.App/DragDrop/InsertIndexCalculator.cs tests/MoTask.App.Tests/InsertIndexCalculatorTests.cs
git commit
```

件名: `feat(app): D&D の挿入位置を決める純粋関数を足す`

---

### Task 2: gong から自前の D&D への切り替え

このタスクは不可分である。ハンドラの型を変えると View の配線が壊れ、View の配線を変えるとハンドラの型が合わなくなるので、抽象型・ビヘイビア・挿入線・ハンドラ・テスト・XAML・パッケージ削除を 1 つのコミットで入れ替える。完了時点でゴーストと自動スクロールはまだ無い（Task 3 と Task 4 で足す）。

**Files:**
- Create: `src/MoTask.App/DragDrop/DragDropContracts.cs`
- Create: `src/MoTask.App/DragDrop/ItemsControlDragHandler.cs`
- Create: `src/MoTask.App/DragDrop/InsertionAdorner.cs`
- Create: `src/MoTask.App/DragDrop/DragDropBehavior.cs`
- Modify: `src/MoTask.App/DragDrop/CardDropHandler.cs`
- Modify: `src/MoTask.App/DragDrop/ColumnDropHandler.cs`
- Modify: `src/MoTask.App/DragDrop/ColumnDragHandler.cs`
- Modify: `src/MoTask.App/DragDrop/DropPositionCalculator.cs`（注釈のみ）
- Modify: `src/MoTask.App/Views/BoardView.xaml`
- Modify: `src/MoTask.App/Views/BoardView.xaml.cs`
- Modify: `src/MoTask.App/Views/ColumnView.xaml`
- Modify: `src/MoTask.App/Views/ColumnView.xaml.cs`
- Modify: `src/MoTask.App/MoTask.App.csproj`
- Modify: `Directory.Packages.props`
- Test: `tests/MoTask.App.Tests/DropHandlerTests.cs`

**Interfaces:**
- Consumes: `InsertIndexCalculator.Calculate(...)`（Task 1）
- Produces:
  - `MoTask.App.DragDrop.IDropContext` — `object? Data { get; }` / `IEnumerable? TargetCollection { get; }` / `int InsertIndex { get; }` / `bool NotHandled { get; set; }` / `DragDropEffects Effects { get; set; }`
  - `MoTask.App.DragDrop.IDragContext` — `FrameworkElement? VisualSource { get; }` / `object? Data { get; set; }` / `DragDropEffects Effects { get; set; }`
  - `MoTask.App.DragDrop.IDropHandler` — `void DragOver(IDropContext)` / `void Drop(IDropContext)`
  - `MoTask.App.DragDrop.IDragHandler` — `bool CanStartDrag(IDragContext)` / `void StartDrag(IDragContext)`
  - `MoTask.App.DragDrop.DropContext(object? data, IEnumerable? targetCollection, int insertIndex)` — `IDropContext` の実装。`NotHandled` の初期値は false、`Effects` の初期値は `DragDropEffects.None`
  - `MoTask.App.DragDrop.DragContext(FrameworkElement? visualSource)` — `IDragContext` の実装
  - `MoTask.App.DragDrop.DragDropBehavior` — 添付プロパティ `IsDragSource` / `IsDropTarget` / `DragSourceIgnore` / `DragHandler` / `DropHandler`、および `SetDropHandler(DependencyObject, IDropHandler)` / `SetDragHandler(DependencyObject, IDragHandler)`
  - `MoTask.App.DragDrop.InsertionAdorner(UIElement adornedElement, Orientation orientation)` — `void MoveTo(Rect target, bool after)`
  - Task 3 と Task 4 が `DragDropBehavior` の内部（`StartDrag` / `HandleOver` / `RemoveDecorations`）に足す

- [ ] **Step 1: 抽象型を追加する**

`src/MoTask.App/DragDrop/DragDropContracts.cs` を新規作成する。

```csharp
using System.Collections;
using System.Windows;

namespace MoTask.App.DragDrop;

/// <summary>
/// ドロップ 1 回分の情報。gong-wpf-dragdrop の IDropInfo のうち、MoTask が実際に
/// 使っていた分だけを写したもの。
/// </summary>
public interface IDropContext
{
    /// <summary>運ばれてきた物。カードなら TaskCardViewModel、列なら ColumnViewModel。</summary>
    object? Data { get; }

    /// <summary>ドロップ先の ItemsControl が束ねている一覧。</summary>
    IEnumerable? TargetCollection { get; }

    /// <summary>この添字の項目の手前に挿す、という意味の値。末尾なら項目数。</summary>
    int InsertIndex { get; }

    /// <summary>
    /// 受けなかったドロップに立てる。DragDropBehavior が e.Handled = !NotHandled を書くので、
    /// 立てておくとルーティングイベントが親へ届く（カード一覧で受けない列のドロップを
    /// 親の列一覧へ通すのに要る）。
    /// </summary>
    bool NotHandled { get; set; }

    DragDropEffects Effects { get; set; }
}

/// <summary>ドラッグ開始 1 回分の情報。gong の IDragInfo のうち使っていた分だけ。</summary>
public interface IDragContext
{
    /// <summary>ドラッグ元として設定された要素。</summary>
    FrameworkElement? VisualSource { get; }

    /// <summary>運ぶ物。ハンドラが載せる。</summary>
    object? Data { get; set; }

    DragDropEffects Effects { get; set; }
}

/// <summary>ドロップを受ける側の判断。View ではなくここに置くことで単体テストできる。</summary>
public interface IDropHandler
{
    void DragOver(IDropContext context);
    void Drop(IDropContext context);
}

/// <summary>ドラッグを始める側の判断。</summary>
public interface IDragHandler
{
    bool CanStartDrag(IDragContext context);
    void StartDrag(IDragContext context);
}

/// <summary>IDropContext の実装。テストからも直接組み立てられるよう素直な作りにする。</summary>
public sealed class DropContext : IDropContext
{
    public DropContext(object? data, IEnumerable? targetCollection, int insertIndex)
    {
        Data = data;
        TargetCollection = targetCollection;
        InsertIndex = insertIndex;
    }

    public object? Data { get; }
    public IEnumerable? TargetCollection { get; }
    public int InsertIndex { get; }
    public bool NotHandled { get; set; }
    public DragDropEffects Effects { get; set; } = DragDropEffects.None;
}

/// <summary>IDragContext の実装。</summary>
public sealed class DragContext : IDragContext
{
    public DragContext(FrameworkElement? visualSource)
    {
        VisualSource = visualSource;
    }

    public FrameworkElement? VisualSource { get; }
    public object? Data { get; set; }
    public DragDropEffects Effects { get; set; } = DragDropEffects.None;
}
```

- [ ] **Step 2: 既定のドラッグハンドラを追加する**

`src/MoTask.App/DragDrop/ItemsControlDragHandler.cs` を新規作成する。カード一覧のように `DragHandler` を指定しない要素で使う。

```csharp
using System.Windows;
using System.Windows.Controls;

namespace MoTask.App.DragDrop;

/// <summary>
/// DragHandler を指定しない ItemsControl の既定。押した点の下にある項目を運ぶ。
/// 選択状態ではなく押した場所を見るので、未選択のカードを掴んでも意図どおりに動く。
/// ドラッグ 1 回ごとに作る（押下点を持つため）。
/// </summary>
internal sealed class ItemsControlDragHandler : IDragHandler
{
    private readonly Point _origin;

    /// <param name="origin">ドラッグ元要素から見た押下点。</param>
    public ItemsControlDragHandler(Point origin)
    {
        _origin = origin;
    }

    public bool CanStartDrag(IDragContext context) => ItemAt(context) is not null;

    public void StartDrag(IDragContext context)
    {
        context.Data = ItemAt(context);
        context.Effects = context.Data is null ? DragDropEffects.None : DragDropEffects.Move;
    }

    private object? ItemAt(IDragContext context)
    {
        if (context.VisualSource is not ItemsControl items) return null;
        if (items.InputHitTest(_origin) is not DependencyObject hit) return null;
        return ItemsControl.ContainerFromElement(items, hit) is FrameworkElement container
            ? container.DataContext
            : null;
    }
}
```

- [ ] **Step 3: 挿入線を追加する**

`src/MoTask.App/DragDrop/InsertionAdorner.cs` を新規作成する。

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MoTask.App.DragDrop;

/// <summary>落ちる場所を示す線 1 本。ドラッグ中だけ AdornerLayer に載る。</summary>
public sealed class InsertionAdorner : Adorner
{
    private readonly Orientation _orientation;
    private readonly Pen _pen;
    private Rect? _target;
    private bool _after;

    public InsertionAdorner(UIElement adornedElement, Orientation orientation)
        : base(adornedElement)
    {
        _orientation = orientation;
        _pen = CreatePen();
        IsHitTestVisible = false;
    }

    /// <param name="target">線を引く基準にする項目の矩形。</param>
    /// <param name="after">true なら項目の後ろ側（下端 / 右端）に引く。</param>
    public void MoveTo(Rect target, bool after)
    {
        _target = target;
        _after = after;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_target is not { } target) return;

        if (_orientation == Orientation.Vertical)
        {
            var y = _after ? target.Bottom : target.Top;
            drawingContext.DrawLine(_pen, new Point(target.Left, y), new Point(target.Right, y));
        }
        else
        {
            var x = _after ? target.Right : target.Left;
            drawingContext.DrawLine(_pen, new Point(x, target.Top), new Point(x, target.Bottom));
        }
    }

    /// <summary>線の色はテーマの Brush.Accent。見つからなければ既定色で描く。</summary>
    private static Pen CreatePen()
    {
        var brush = Application.Current?.TryFindResource("Brush.Accent") as Brush
                    ?? new SolidColorBrush(Color.FromRgb(0x4C, 0x8E, 0xFF));
        if (brush.CanFreeze) brush.Freeze();
        var pen = new Pen(brush, 2);
        pen.Freeze();
        return pen;
    }
}
```

- [ ] **Step 4: 添付ビヘイビアを追加する**

`src/MoTask.App/DragDrop/DragDropBehavior.cs` を新規作成する。

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace MoTask.App.DragDrop;

/// <summary>
/// WPF 標準の D&amp;D を MoTask の IDropHandler / IDragHandler につなぐ添付ビヘイビア。
/// gong-wpf-dragdrop を置き換えたもの。
/// 設計: docs/superpowers/specs/2026-09-12-drop-third-party-oss-deps-design.md
/// </summary>
public static class DragDropBehavior
{
    /// <summary>
    /// DataObject に入れる目印。実体は _payload に置く。DataObject に ViewModel を
    /// 直接入れると、ウィンドウの外へカーソルが出たときに OLE がシリアライズを試みて失敗しうる。
    /// </summary>
    private const string PayloadFormat = "MoTask.DragDrop.Payload";

    /// <summary>いま運んでいる物。アプリは単一インスタンスなので静的でも取り違えは起きない。</summary>
    private static object? _payload;

    /// <summary>ドラッグ元要素から見た押下点。閾値を超えるまで開始を待つために持つ。</summary>
    private static Point? _origin;

    private static InsertionAdorner? _insertion;

    // ---- 添付プロパティ ----

    public static readonly DependencyProperty IsDragSourceProperty = DependencyProperty.RegisterAttached(
        "IsDragSource", typeof(bool), typeof(DragDropBehavior),
        new PropertyMetadata(false, OnIsDragSourceChanged));

    public static void SetIsDragSource(DependencyObject element, bool value)
        => element.SetValue(IsDragSourceProperty, value);

    public static bool GetIsDragSource(DependencyObject element)
        => (bool)element.GetValue(IsDragSourceProperty);

    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(DragDropBehavior),
        new PropertyMetadata(false, OnIsDropTargetChanged));

    public static void SetIsDropTarget(DependencyObject element, bool value)
        => element.SetValue(IsDropTargetProperty, value);

    public static bool GetIsDropTarget(DependencyObject element)
        => (bool)element.GetValue(IsDropTargetProperty);

    /// <summary>この要素の上で押し始めたドラッグは開始しない（列ヘッダーの操作部品に付ける）。</summary>
    public static readonly DependencyProperty DragSourceIgnoreProperty = DependencyProperty.RegisterAttached(
        "DragSourceIgnore", typeof(bool), typeof(DragDropBehavior), new PropertyMetadata(false));

    public static void SetDragSourceIgnore(DependencyObject element, bool value)
        => element.SetValue(DragSourceIgnoreProperty, value);

    public static bool GetDragSourceIgnore(DependencyObject element)
        => (bool)element.GetValue(DragSourceIgnoreProperty);

    public static readonly DependencyProperty DragHandlerProperty = DependencyProperty.RegisterAttached(
        "DragHandler", typeof(IDragHandler), typeof(DragDropBehavior), new PropertyMetadata(null));

    public static void SetDragHandler(DependencyObject element, IDragHandler? value)
        => element.SetValue(DragHandlerProperty, value);

    public static IDragHandler? GetDragHandler(DependencyObject element)
        => (IDragHandler?)element.GetValue(DragHandlerProperty);

    public static readonly DependencyProperty DropHandlerProperty = DependencyProperty.RegisterAttached(
        "DropHandler", typeof(IDropHandler), typeof(DragDropBehavior), new PropertyMetadata(null));

    public static void SetDropHandler(DependencyObject element, IDropHandler? value)
        => element.SetValue(DropHandlerProperty, value);

    public static IDropHandler? GetDropHandler(DependencyObject element)
        => (IDropHandler?)element.GetValue(DropHandlerProperty);

    // ---- ドラッグ元 ----

    private static void OnIsDragSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.PreviewMouseLeftButtonDown -= OnSourceMouseDown;
        element.PreviewMouseMove -= OnSourceMouseMove;
        element.PreviewMouseLeftButtonUp -= OnSourceMouseUp;

        if (e.NewValue is not true) return;

        element.PreviewMouseLeftButtonDown += OnSourceMouseDown;
        element.PreviewMouseMove += OnSourceMouseMove;
        element.PreviewMouseLeftButtonUp += OnSourceMouseUp;
    }

    private static void OnSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var element = (FrameworkElement)sender;
        _origin = IsIgnored(e.OriginalSource as DependencyObject, element)
            ? null
            : e.GetPosition(element);
    }

    private static void OnSourceMouseUp(object sender, MouseButtonEventArgs e) => _origin = null;

    private static void OnSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (_origin is not { } origin) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _origin = null;
            return;
        }

        var element = (FrameworkElement)sender;
        var now = e.GetPosition(element);
        if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _origin = null;
        StartDrag(element, origin);
    }

    private static void StartDrag(FrameworkElement element, Point origin)
    {
        var handler = GetDragHandler(element) ?? new ItemsControlDragHandler(origin);
        var context = new DragContext(element);
        if (!handler.CanStartDrag(context)) return;

        handler.StartDrag(context);
        if (context.Data is null) return;

        _payload = context.Data;
        try
        {
            var data = new DataObject(PayloadFormat, PayloadFormat);
            System.Windows.DragDrop.DoDragDrop(element, data, context.Effects);
        }
        finally
        {
            _payload = null;
            RemoveDecorations();
        }
    }

    /// <summary>押した場所からドラッグ元まで遡って、DragSourceIgnore が立った要素があるか。</summary>
    private static bool IsIgnored(DependencyObject? from, FrameworkElement source)
    {
        for (var node = from; node is not null; node = ParentOf(node))
        {
            if (GetDragSourceIgnore(node)) return true;
            if (ReferenceEquals(node, source)) break;
        }
        return false;
    }

    /// <summary>Visual でない要素に VisualTreeHelper を使うと落ちるので、論理ツリーへ逃がす。</summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    // ---- ドロップ先 ----

    private static void OnIsDropTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.DragEnter -= OnDragEnter;
        element.DragOver -= OnDragOver;
        element.DragLeave -= OnDragLeave;
        element.Drop -= OnDrop;

        if (e.NewValue is not true)
        {
            element.AllowDrop = false;
            return;
        }

        element.AllowDrop = true;
        element.DragEnter += OnDragEnter;
        element.DragOver += OnDragOver;
        element.DragLeave += OnDragLeave;
        element.Drop += OnDrop;
    }

    /// <summary>
    /// IDropHandler に DragEnter は無い。今の CardDropHandler / ColumnDropHandler の
    /// DragEnter が DragOver への転送でしかないことに合わせて、ここで回す。
    /// </summary>
    private static void OnDragEnter(object sender, DragEventArgs e) => HandleOver(sender, e);

    private static void OnDragOver(object sender, DragEventArgs e) => HandleOver(sender, e);

    /// <summary>ハンドラは呼ばない。装飾を外すだけ。</summary>
    private static void OnDragLeave(object sender, DragEventArgs e) => RemoveInsertion();

    private static void HandleOver(object sender, DragEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (GetDropHandler(element) is not { } handler) return;

        var context = BuildContext(element, e);
        handler.DragOver(context);
        e.Effects = context.Effects;
        e.Handled = !context.NotHandled;

        if (context.NotHandled) RemoveInsertion();
        else ShowInsertion(element, context.InsertIndex);
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        RemoveInsertion();

        var element = (FrameworkElement)sender;
        if (GetDropHandler(element) is not { } handler) return;

        var context = BuildContext(element, e);
        handler.Drop(context);
        e.Effects = context.Effects;
        e.Handled = !context.NotHandled;
    }

    private static DropContext BuildContext(FrameworkElement element, DragEventArgs e)
    {
        var items = element as ItemsControl;
        var containers = RealizedContainers(items, element);
        var index = InsertIndexCalculator.Calculate(containers, e.GetPosition(element), OrientationOf(items));
        return new DropContext(_payload, items?.ItemsSource, index);
    }

    /// <summary>
    /// 実体化済みの項目コンテナを、本来の添字つきで集める。ListBox は仮想化するので
    /// 画面外の項目は null になり、ここには現れない。
    /// </summary>
    private static IReadOnlyList<(int Index, Rect Bounds)> RealizedContainers(
        ItemsControl? items, FrameworkElement relativeTo)
    {
        if (items is null) return Array.Empty<(int, Rect)>();

        var result = new List<(int, Rect)>();
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            if (!container.IsVisible) continue;

            var origin = container.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
            result.Add((i, new Rect(origin, new Size(container.ActualWidth, container.ActualHeight))));
        }
        return result;
    }

    /// <summary>項目パネルの向き。カード一覧は縦、列一覧は横。</summary>
    private static Orientation OrientationOf(ItemsControl? items)
    {
        if (items is null || items.Items.Count == 0) return Orientation.Vertical;
        if (items.ItemContainerGenerator.ContainerFromIndex(0) is not DependencyObject container) return Orientation.Vertical;

        return VisualTreeHelper.GetParent(container) switch
        {
            VirtualizingStackPanel virtualizing => virtualizing.Orientation,
            StackPanel stack => stack.Orientation,
            WrapPanel wrap => wrap.Orientation,
            _ => Orientation.Vertical,
        };
    }

    // ---- 装飾 ----

    private static void ShowInsertion(FrameworkElement element, int insertIndex)
    {
        if (element is not ItemsControl items)
        {
            RemoveInsertion();
            return;
        }

        var containers = RealizedContainers(items, element);
        if (containers.Count == 0)
        {
            RemoveInsertion();
            return;
        }

        Rect? target = null;
        var after = false;
        if (insertIndex > containers[^1].Index)
        {
            target = containers[^1].Bounds;
            after = true;
        }
        else
        {
            foreach (var container in containers)
            {
                if (container.Index != insertIndex) continue;
                target = container.Bounds;
                break;
            }
        }

        if (target is not { } bounds)
        {
            RemoveInsertion();
            return;
        }

        if (_insertion is null || !ReferenceEquals(_insertion.AdornedElement, element))
        {
            RemoveInsertion();
            if (AdornerLayer.GetAdornerLayer(element) is not { } layer) return;
            _insertion = new InsertionAdorner(element, OrientationOf(items));
            layer.Add(_insertion);
        }

        _insertion.MoveTo(bounds, after);
    }

    private static void RemoveInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(_insertion.AdornedElement)?.Remove(_insertion);
        _insertion = null;
    }

    /// <summary>ドラッグが終わったときに必ず呼ぶ。Task 3 と Task 4 でここに足す。</summary>
    private static void RemoveDecorations() => RemoveInsertion();
}
```

- [ ] **Step 5: ハンドラ 3 つを新しい型へ載せ替える**

`src/MoTask.App/DragDrop/CardDropHandler.cs` を次の内容で全面的に置き換える。判断ロジックは 1 行も変えていない。`using GongSolutions.Wpf.DragDrop;` と `DropTargetAdorner` への代入と `DragEnter` / `DragLeave` / `DropHint` が消えただけである。

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>カードの列内並び替えと列間移動。ドロップ時に MoveTask を1回だけ呼ぶ。</summary>
public sealed class CardDropHandler : IDropHandler
{
    private readonly BoardViewModel _board;

    public CardDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropContext dropInfo)
    {
        // カード以外（列ヘッダーのドラッグ）は受けない。NotHandled を立てて親の
        // ItemsControl（列の並び替え）まで通す。ここで握ると列をどこにも落とせなくなる。
        if (!CanAccept(dropInfo))
        {
            dropInfo.NotHandled = true;
            return;
        }
        dropInfo.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropContext dropInfo)
    {
        // ここで受けないドロップは DragOver と同じく NotHandled を立てて親へ返す。
        // DragDropBehavior は最後に e.Handled = !dropInfo.NotHandled を書くので、立てずに抜けると
        // カード一覧が Drop ルーティングイベントを握ってしまい、列の並び替え（親 ColumnsHost の
        // ColumnDropHandler）まで届かない。カード一覧は列のほぼ全面を覆うので、列ヘッダーを
        // 別の列へ落とすと何も起きなくなる。
        if (dropInfo.Data is not TaskCardViewModel card)
        {
            dropInfo.NotHandled = true;
            return;
        }
        if (dropInfo.TargetCollection is not ObservableCollection<TaskCardViewModel> cards)
        {
            dropInfo.NotHandled = true;
            return;
        }
        var target = _board.Columns.FirstOrDefault(c => ReferenceEquals(c.Cards, cards));
        if (target is null)
        {
            dropInfo.NotHandled = true;
            return;
        }

        var position = DropPositionCalculator.ToPosition(target.Cards, target.AllCards, card, dropInfo.InsertIndex);
        if (position is null)
        {
            dropInfo.NotHandled = true;
            return;
        }

        // 裁定6: 同じ列の今の位置へ落としただけなら、保存を往復させない。
        var source = _board.Columns.FirstOrDefault(c => c.AllCards.Contains(card));
        if (ReferenceEquals(source, target) && DropPositionCalculator.IsNoOp(target.AllCards, card, position.Value))
        {
            dropInfo.NotHandled = true;
            return;
        }

        // Drop は Task を返せないので board 側に観測させる。discard にすると保存の失敗が消える。
        _board.RunGuarded(() => _board.MoveCardAsync(card, target, position.Value));
    }

    private static bool CanAccept(IDropContext dropInfo)
        => dropInfo.Data is TaskCardViewModel && dropInfo.TargetCollection is ObservableCollection<TaskCardViewModel>;
}
```

`src/MoTask.App/DragDrop/ColumnDropHandler.cs` を次の内容で全面的に置き換える。

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>列の並び替え。列ヘッダーから始めたドラッグを列の ItemsControl で受ける。</summary>
public sealed class ColumnDropHandler : IDropHandler
{
    private readonly BoardViewModel _board;

    public ColumnDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropContext dropInfo)
    {
        // 列以外（カードのドラッグ）はここでは受けない。握らず親へ返す。
        if (!CanAccept(dropInfo))
        {
            dropInfo.NotHandled = true;
            return;
        }
        dropInfo.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropContext dropInfo)
    {
        // DragOver と同じく、受けないドロップは NotHandled を立てて返す（立てないと
        // DragDropBehavior が e.Handled = true にしてしまう）。ここは最上位の drop target なので
        // 実害は小さいが、「受けなかったのに握る」状態を残さない。
        if (dropInfo.Data is not ColumnViewModel moving)
        {
            dropInfo.NotHandled = true;
            return;
        }
        if (dropInfo.TargetCollection is not ObservableCollection<ColumnViewModel>)
        {
            dropInfo.NotHandled = true;
            return;
        }

        var current = _board.Columns.ToList();
        var order = DropPositionCalculator.Reorder(current, moving, dropInfo.InsertIndex);
        // 裁定6 と同じ理由: 並びが変わらないドロップは保存を往復させない。
        if (order.SequenceEqual(current))
        {
            dropInfo.NotHandled = true;
            return;
        }

        _board.RunGuarded(() => _board.ReorderColumnsAsync(order));
    }

    private static bool CanAccept(IDropContext dropInfo)
        => dropInfo.Data is ColumnViewModel && dropInfo.TargetCollection is ObservableCollection<ColumnViewModel>;
}
```

`src/MoTask.App/DragDrop/ColumnDragHandler.cs` を次の内容で全面的に置き換える。

```csharp
using System.Windows;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>
/// 列ヘッダー（ItemsControl ではない要素）からのドラッグ。既定のハンドラは ItemsControl の
/// 項目から Data を組み立てるので、ここでは要素の DataContext から列を直接載せる。
/// </summary>
public sealed class ColumnDragHandler : IDragHandler
{
    public bool CanStartDrag(IDragContext dragInfo)
        => dragInfo.VisualSource?.DataContext is ColumnViewModel;

    public void StartDrag(IDragContext dragInfo)
    {
        var column = dragInfo.VisualSource?.DataContext as ColumnViewModel;
        dragInfo.Data = column;
        dragInfo.Effects = column is null ? DragDropEffects.None : DragDropEffects.Move;
    }
}
```

- [ ] **Step 6: DropPositionCalculator の注釈から gong の名前を消す**

`src/MoTask.App/DragDrop/DropPositionCalculator.cs` の 2 箇所を直す。ロジックには触らない。

置換 1（クラスの注釈）:

```
/// D&amp;D の挿入位置を Core が期待する position へ直す純粋な計算。UI スレッドも
/// gong-wpf-dragdrop の型も要らないので、そのまま単体テストできる。
```

を

```
/// D&amp;D の挿入位置を Core が期待する position へ直す純粋な計算。UI スレッドも
/// WPF の型も要らないので、そのまま単体テストできる。
```

に。

置換 2（`ToPosition` の注釈）:

```
    /// Gong の InsertIndex（表示カード列での挿入位置）を、Core の position
```

を

```
    /// InsertIndex（表示カード列での挿入位置）を、Core の position
```

に。

- [ ] **Step 7: DropHandlerTests を自前の型へ書き換える**

`tests/MoTask.App.Tests/DropHandlerTests.cs` に対し、次の 4 種類の置換を行う。NSubstitute による `IBoardService` などの差し替えはそのまま残す（2 本目の計画の仕事）。

置換 1 — using から gong を消し、`DragDropEffects` のために `System.Windows` を足す。

```csharp
using System.Collections;
using FluentAssertions;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.Ai;
```

を

```csharp
using System.Collections;
using System.Windows;
using FluentAssertions;
using MoTask.App.Ai;
```

に。

置換 2 — `Info` ヘルパーを自前の `DropContext` を直接組み立てる形にする。戻り値の型も `DropContext` にして、テスト側が `NotHandled` と `Effects` を読めるようにする。

```csharp
    private static IDropInfo Info(object? data, IEnumerable? targetCollection, int insertIndex)
    {
        var info = Substitute.For<IDropInfo>();
        info.Data.Returns(data);
        info.TargetCollection.Returns(targetCollection);
        info.InsertIndex.Returns(insertIndex);
        return info;
    }
```

を

```csharp
    private static DropContext Info(object? data, IEnumerable? targetCollection, int insertIndex)
        => new(data, targetCollection, insertIndex);
```

に。

置換 3 — 「握らずに親へ返した」ことの検証。ファイル内に 6 箇所ある次の行を、

```csharp
        info.Received().NotHandled = true;
```

すべて

```csharp
        info.NotHandled.Should().BeTrue();
```

に置き換える。`CardDrop_AtOwnPosition_DoesNotTouchTheService` にある

```csharp
        info.Received().NotHandled = true;   // 何もしなかったのに Drop を握らない
```

は

```csharp
        info.NotHandled.Should().BeTrue();   // 何もしなかったのに Drop を握らない
```

に。

置換 4 — 「Effects に触っていない」ことの検証。ファイル内に 2 箇所ある次の行を、

```csharp
        info.DidNotReceiveWithAnyArgs().Effects = default;
```

両方とも

```csharp
        info.Effects.Should().Be(DragDropEffects.None);
```

に置き換える。`DropContext.Effects` の初期値が `DragDropEffects.None` なので、ハンドラが触らなければこの値のまま残る。

置換 5 — `CardDrop_WithColumnData_LeavesEventForTheParent` の注釈から gong の名前を消す。

```csharp
    /// 列のドラッグはカード列で握らずに親へ返す。DragOver だけでなく Drop も同じで、
    /// Gong は NotHandled を立てないと e.Handled = true にしてしまう。カード一覧は列のほぼ
```

を

```csharp
    /// 列のドラッグはカード列で握らずに親へ返す。DragOver だけでなく Drop も同じで、
    /// NotHandled を立てないと e.Handled = true になる。カード一覧は列のほぼ
```

に。

- [ ] **Step 8: テストが通ることを確かめる**

```
dotnet test tests/MoTask.App.Tests --filter "FullyQualifiedName~DropHandlerTests|FullyQualifiedName~DropPositionCalculatorTests|FullyQualifiedName~InsertIndexCalculatorTests"
```

Expected: PASS。この時点ではまだ View が gong を呼んでいるのでビルドは通る。

- [ ] **Step 9: XAML の名前空間と属性を差し替える**

`src/MoTask.App/Views/BoardView.xaml` の 6 行目、

```xml
             xmlns:dd="urn:gong-wpf-dragdrop"
```

を

```xml
             xmlns:dd="clr-namespace:MoTask.App.DragDrop"
```

に。同ファイル 14 行目、

```xml
      <ItemsControl x:Name="ColumnsHost" ItemsSource="{Binding Columns}" dd:DragDrop.IsDropTarget="True">
```

を

```xml
      <ItemsControl x:Name="ColumnsHost" ItemsSource="{Binding Columns}" dd:DragDropBehavior.IsDropTarget="True">
```

に。

`src/MoTask.App/Views/ColumnView.xaml` の 6 行目、

```xml
             xmlns:dd="urn:gong-wpf-dragdrop"
```

を

```xml
             xmlns:dd="clr-namespace:MoTask.App.DragDrop"
```

に。同ファイル 19 行目、

```xml
              dd:DragDrop.IsDragSource="True" dd:DragDrop.UseDefaultDragAdorner="True">
```

を

```xml
              dd:DragDropBehavior.IsDragSource="True">
```

に。同ファイル 24 行目、50 行目、67 行目にある 3 箇所の

```xml
dd:DragDrop.DragSourceIgnore="True"
```

をすべて

```xml
dd:DragDropBehavior.DragSourceIgnore="True"
```

に。同ファイル 90〜91 行目、

```xml
               dd:DragDrop.IsDragSource="True" dd:DragDrop.IsDropTarget="True"
               dd:DragDrop.UseDefaultDragAdorner="True">
```

を

```xml
               dd:DragDropBehavior.IsDragSource="True" dd:DragDropBehavior.IsDropTarget="True">
```

に。

`UseDefaultDragAdorner` は消す。ゴーストは Task 3 で常に出すようにする。

- [ ] **Step 10: コードビハインドの配線を差し替える**

`src/MoTask.App/Views/BoardView.xaml.cs` の using から

```csharp
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;
```

を削除し、`AttachDragDrop` の中身を

```csharp
        GongDragDrop.SetDropHandler(ColumnsHost, new ColumnDropHandler(vm));
```

から

```csharp
        DragDropBehavior.SetDropHandler(ColumnsHost, new ColumnDropHandler(vm));
```

に。

`src/MoTask.App/Views/ColumnView.xaml.cs` の using から

```csharp
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;
```

を削除し、`AttachDragDrop` の注釈と中身を次に置き換える。

```csharp
    /// <summary>
    /// D&amp;D のハンドラはここで挿す。ViewModel に持たせると ViewModels が
    /// DragDrop に依存してしまう（依存は DragDrop → ViewModels の一方通行）。
    /// </summary>
    private void AttachDragDrop()
    {
        if (Vm is not { } vm) return;
        DragDropBehavior.SetDropHandler(CardList, new CardDropHandler(vm.Board));
        DragDropBehavior.SetDragHandler(Header, new ColumnDragHandler());
    }
```

- [ ] **Step 11: パッケージ参照を削除する**

`Directory.Packages.props` から次の 1 行を削除する。

```xml
    <PackageVersion Include="gong-wpf-dragdrop" Version="4.0.0" />
```

`src/MoTask.App/MoTask.App.csproj` から次の 1 行を削除する。

```xml
    <PackageReference Include="gong-wpf-dragdrop" />
```

- [ ] **Step 12: ビルドと全テストを通す**

MoTask.exe が起動していないことを確かめてから実行する。

```
dotnet build
```

Expected: 0 警告 0 エラー

```
dotnet test tests/MoTask.Core.Tests
dotnet test tests/MoTask.Data.Tests
dotnet test tests/MoTask.App.Tests
dotnet test tests/MoTask.Mcp.Tests
```

Expected: それぞれ 294 / 41 / 395 + 19（Task 1 の新規分）/ 21 がすべて緑

- [ ] **Step 13: gong が残っていないことを確かめる**

```
grep -rn --include=*.cs --include=*.xaml --include=*.csproj --include=*.props -iE "gong|GongSolutions" src tests Directory.Packages.props | grep -v "/obj/" | grep -v "/bin/"
```

Expected: 何も出ない（設計仕様の文書に名前が残るのは構わない。上のコマンドは docs を見ていない）

- [ ] **Step 14: 手動で動きを確かめる**

`dotnet run --project src/MoTask.App` でアプリを起動し、次を確認する。

- [ ] カードを同じ列の中で上下に並び替えられる。順序が保存される
- [ ] カードを別の列へ移せる。移動先の狙った位置に入る
- [ ] カードを今いる場所へ落としても保存が走らない
- [ ] 列ヘッダーを掴んで列を並び替えられる。順序が保存される
- [ ] 列ヘッダーのメニューボタンを押しても列を掴まない
- [ ] 列の改名欄・WIP 入力欄をドラッグしても列を掴まない（文字選択ができる）
- [ ] カードを列ヘッダーの上へ落としても列の並びが壊れない
- [ ] ドラッグ中、挿入線が落ちる位置に出る
- [ ] Esc で中断すると挿入線が消え、何も保存されない

確認が終わったらアプリを閉じる。

- [ ] **Step 15: コミットする**

```bash
git add -A
git commit
```

件名: `feat(app): D&D を WPF 標準 API の自前実装に置き換える`

本文には「gong-wpf-dragdrop への依存を外した。ハンドラの判断ロジックは変えていない。ゴーストと自動スクロールは後続で足す」ことを書く。

---

### Task 3: ドラッグ中のゴースト

gong の `UseDefaultDragAdorner` が出していた「掴んだ物の半透明の写しがカーソルに付いてくる」表示を取り戻す。これが無いと何を掴んでいるか分からず、操作感が明らかに落ちる。

**Files:**
- Create: `src/MoTask.App/DragDrop/NativeCursor.cs`
- Create: `src/MoTask.App/DragDrop/DragGhostAdorner.cs`
- Modify: `src/MoTask.App/DragDrop/DragDropBehavior.cs`

**Interfaces:**
- Consumes: `DragDropBehavior.StartDrag` / `.RemoveDecorations` / `.OnIsDragSourceChanged`（Task 2）
- Produces: `MoTask.App.DragDrop.DragGhostAdorner(UIElement adornedElement, FrameworkElement source)` — `void MoveTo(Point position)`

- [ ] **Step 1: カーソル位置を取る P/Invoke を足す**

`src/MoTask.App/DragDrop/NativeCursor.cs` を新規作成する。

`[LibraryImport]` は使わない。`out` の構造体を扱う生成コードが unsafe を必要とし、`AllowUnsafeBlocks` をプロジェクト全体で有効にする羽目になるためである（SYSLIB1062）。`[DllImport]` は警告なしで通ることを確認済み。

```csharp
using System.Runtime.InteropServices;

namespace MoTask.App.DragDrop;

/// <summary>
/// ドラッグ中のカーソル位置。WPF の GiveFeedback は DragEventArgs を渡してくれないので、
/// 画面座標を Win32 から直接取る。ドラッグのゴーストを追従させるためだけに使う。
/// </summary>
internal static class NativeCursor
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);
}
```

- [ ] **Step 2: ゴーストの Adorner を足す**

`src/MoTask.App/DragDrop/DragGhostAdorner.cs` を新規作成する。

```csharp
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MoTask.App.DragDrop;

/// <summary>掴んでいる物の半透明の写し。カーソルに追従する。</summary>
public sealed class DragGhostAdorner : Adorner
{
    /// <summary>カーソルから写しの左上までのずらし幅。真上に置くと本文が隠れる。</summary>
    private const double Offset = 8;

    private readonly Brush _brush;
    private readonly Size _size;
    private Point? _position;

    /// <param name="adornedElement">ウィンドウ直下の要素。ここの AdornerLayer に載せる。</param>
    /// <param name="source">写す元。掴んでいるカードや列ヘッダー。</param>
    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        // 生きた Visual を写す VisualBrush は Freeze できないので、そのまま持つ。
        _brush = new VisualBrush(source) { Opacity = 0.65 };
        _size = new Size(source.ActualWidth, source.ActualHeight);
        IsHitTestVisible = false;
    }

    /// <param name="position">adornedElement から見たカーソル位置。</param>
    public void MoveTo(Point position)
    {
        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_position is not { } position) return;
        if (_size.Width <= 0 || _size.Height <= 0) return;

        drawingContext.DrawRectangle(_brush, null, new Rect(
            new Point(position.X + Offset, position.Y + Offset), _size));
    }
}
```

- [ ] **Step 3: ビヘイビアにゴーストを組み込む**

`src/MoTask.App/DragDrop/DragDropBehavior.cs` に次の 4 つの変更を加える。

変更 1 — フィールドに 1 行足す。`private static InsertionAdorner? _insertion;` の直後に置く。

```csharp
    private static DragGhostAdorner? _ghost;
```

変更 2 — `OnIsDragSourceChanged` に `GiveFeedback` の付け外しを足す。既存の 3 つの `-=` の並びに 1 行、3 つの `+=` の並びに 1 行を足す。結果は次のとおり。

```csharp
    private static void OnIsDragSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.PreviewMouseLeftButtonDown -= OnSourceMouseDown;
        element.PreviewMouseMove -= OnSourceMouseMove;
        element.PreviewMouseLeftButtonUp -= OnSourceMouseUp;
        element.GiveFeedback -= OnGiveFeedback;

        if (e.NewValue is not true) return;

        element.PreviewMouseLeftButtonDown += OnSourceMouseDown;
        element.PreviewMouseMove += OnSourceMouseMove;
        element.PreviewMouseLeftButtonUp += OnSourceMouseUp;
        element.GiveFeedback += OnGiveFeedback;
    }
```

変更 3 — `StartDrag` でゴーストを出す。`_payload = context.Data;` の直後に 1 行足す。結果は次のとおり。

```csharp
    private static void StartDrag(FrameworkElement element, Point origin)
    {
        var handler = GetDragHandler(element) ?? new ItemsControlDragHandler(origin);
        var context = new DragContext(element);
        if (!handler.CanStartDrag(context)) return;

        handler.StartDrag(context);
        if (context.Data is null) return;

        _payload = context.Data;
        ShowGhost(element, origin);
        try
        {
            var data = new DataObject(PayloadFormat, PayloadFormat);
            System.Windows.DragDrop.DoDragDrop(element, data, context.Effects);
        }
        finally
        {
            _payload = null;
            RemoveDecorations();
        }
    }
```

変更 4 — 「装飾」の節に次を足し、`RemoveDecorations` を書き換える。

```csharp
    /// <summary>
    /// ゴーストを出す。載せ先はウィンドウ直下なので、ドロップ先の外へカーソルが出ても消えない。
    /// </summary>
    private static void ShowGhost(FrameworkElement element, Point origin)
    {
        if (GhostSourceOf(element, origin) is not { } source) return;
        if (Window.GetWindow(element)?.Content is not UIElement root) return;
        if (AdornerLayer.GetAdornerLayer(root) is not { } layer) return;

        RemoveGhost();
        _ghost = new DragGhostAdorner(root, source);
        layer.Add(_ghost);
    }

    /// <summary>写す元。ItemsControl なら押した点の項目、そうでなければ要素そのもの（列ヘッダー）。</summary>
    private static FrameworkElement? GhostSourceOf(FrameworkElement element, Point origin)
    {
        if (element is not ItemsControl items) return element;
        if (items.InputHitTest(origin) is not DependencyObject hit) return null;
        return ItemsControl.ContainerFromElement(items, hit) as FrameworkElement;
    }

    /// <summary>
    /// ゴーストの追従。GiveFeedback はドロップ先の有無にかかわらず continuous に起きるので、
    /// ドロップ先の隙間にカーソルがあってもゴーストが止まらない。
    /// </summary>
    private static void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_ghost is null) return;
        if (!NativeCursor.GetCursorPos(out var point)) return;

        var root = (UIElement)_ghost.AdornedElement;
        _ghost.MoveTo(root.PointFromScreen(new Point(point.X, point.Y)));
    }

    private static void RemoveGhost()
    {
        if (_ghost is null) return;
        AdornerLayer.GetAdornerLayer(_ghost.AdornedElement)?.Remove(_ghost);
        _ghost = null;
    }

    /// <summary>ドラッグが終わったときに必ず呼ぶ。</summary>
    private static void RemoveDecorations()
    {
        RemoveInsertion();
        RemoveGhost();
    }
```

`RemoveDecorations` は 1 つだけ残す。Task 2 で置いた `private static void RemoveDecorations() => RemoveInsertion();` は消す。

- [ ] **Step 4: ビルドと全テストを通す**

```
dotnet build
```

Expected: 0 警告 0 エラー

```
dotnet test tests/MoTask.App.Tests
```

Expected: 全緑

- [ ] **Step 5: 手動で動きを確かめる**

`dotnet run --project src/MoTask.App` で起動し、次を確認する。

- [ ] カードを掴むと半透明の写しがカーソルに付いてくる
- [ ] 列ヘッダーを掴むとヘッダーの写しが付いてくる
- [ ] ドロップ先の外（列と列のあいだの余白やウィンドウの端）へカーソルを出してもゴーストが固まらない
- [ ] ドロップしたらゴーストが消える
- [ ] Esc で中断してもゴーストが消える
- [ ] Task 2 で確かめた 9 項目がすべて変わらず動く

確認が終わったらアプリを閉じる。

- [ ] **Step 6: コミットする**

```bash
git add -A
git commit
```

件名: `feat(app): ドラッグ中のゴーストを自前の Adorner で出す`

---

### Task 4: 自動スクロール

列が画面幅に収まらないとき、またはカードが多い列の中で、掴んだまま端へ寄せるとスクロールする。これが無いと見えている範囲にしか落とせない。

**Files:**
- Modify: `src/MoTask.App/DragDrop/DragDropBehavior.cs`

**Interfaces:**
- Consumes: `DragDropBehavior.HandleOver` / `.OnDragLeave` / `.RemoveDecorations`（Task 2、Task 3）
- Produces: なし（内部だけで閉じる）

- [ ] **Step 1: ビヘイビアに自動スクロールを組み込む**

`src/MoTask.App/DragDrop/DragDropBehavior.cs` に次の 5 つの変更を加える。

変更 1 — using に 1 行足す。

```csharp
using System.Windows.Threading;
```

変更 2 — フィールドに次を足す。`private static DragGhostAdorner? _ghost;` の直後に置く。

```csharp
    /// <summary>端から何 dip 以内でスクロールを始めるか。</summary>
    private const double AutoScrollMargin = 24;

    /// <summary>スクロールの刻み。1 tick ごとに ScrollViewer の Line 系を 1 回呼ぶ。</summary>
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(50);

    private enum ScrollDirection { None, Left, Right, Up, Down }

    private static DispatcherTimer? _autoScrollTimer;
    private static ScrollViewer? _autoScrollViewer;
    private static ScrollDirection _autoScrollDirection = ScrollDirection.None;
```

変更 3 — `HandleOver` の末尾で自動スクロールを更新する。結果は次のとおり。

```csharp
    private static void HandleOver(object sender, DragEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (GetDropHandler(element) is not { } handler) return;

        var context = BuildContext(element, e);
        handler.DragOver(context);
        e.Effects = context.Effects;
        e.Handled = !context.NotHandled;

        if (context.NotHandled)
        {
            RemoveInsertion();
            StopAutoScroll();
            return;
        }

        ShowInsertion(element, context.InsertIndex);
        UpdateAutoScroll(element, e);
    }
```

変更 4 — `OnDragLeave` でも止める。

```csharp
    /// <summary>ハンドラは呼ばない。装飾を外してスクロールを止めるだけ。</summary>
    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        RemoveInsertion();
        StopAutoScroll();
    }
```

変更 5 — 「装飾」の節の末尾に次を足し、`RemoveDecorations` にも 1 行足す。

```csharp
    /// <summary>
    /// カーソルが端の近くにいる間だけスクロールを回す。
    /// ScrollViewer の Line 系を使うので、物理スクロールと論理スクロール（仮想化 ListBox）の
    /// どちらでも同じように動く。
    /// </summary>
    private static void UpdateAutoScroll(FrameworkElement element, DragEventArgs e)
    {
        if (FindScrollViewer(element) is not { } viewer)
        {
            StopAutoScroll();
            return;
        }

        var position = e.GetPosition(viewer);
        var direction = DirectionFor(viewer, position);
        if (direction == ScrollDirection.None)
        {
            StopAutoScroll();
            return;
        }

        _autoScrollViewer = viewer;
        _autoScrollDirection = direction;

        if (_autoScrollTimer is not null) return;
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = AutoScrollInterval,
        };
        _autoScrollTimer.Tick += OnAutoScrollTick;
        _autoScrollTimer.Start();
    }

    private static ScrollDirection DirectionFor(ScrollViewer viewer, Point position)
    {
        if (viewer.ScrollableHeight > 0)
        {
            if (position.Y < AutoScrollMargin) return ScrollDirection.Up;
            if (position.Y > viewer.ActualHeight - AutoScrollMargin) return ScrollDirection.Down;
        }
        if (viewer.ScrollableWidth > 0)
        {
            if (position.X < AutoScrollMargin) return ScrollDirection.Left;
            if (position.X > viewer.ActualWidth - AutoScrollMargin) return ScrollDirection.Right;
        }
        return ScrollDirection.None;
    }

    private static void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_autoScrollViewer is not { } viewer)
        {
            StopAutoScroll();
            return;
        }

        switch (_autoScrollDirection)
        {
            case ScrollDirection.Up: viewer.LineUp(); break;
            case ScrollDirection.Down: viewer.LineDown(); break;
            case ScrollDirection.Left: viewer.LineLeft(); break;
            case ScrollDirection.Right: viewer.LineRight(); break;
            default: StopAutoScroll(); break;
        }
    }

    private static void StopAutoScroll()
    {
        if (_autoScrollTimer is not null)
        {
            _autoScrollTimer.Stop();
            _autoScrollTimer.Tick -= OnAutoScrollTick;
            _autoScrollTimer = null;
        }
        _autoScrollViewer = null;
        _autoScrollDirection = ScrollDirection.None;
    }

    /// <summary>
    /// スクロールさせる ScrollViewer。カード一覧（ListBox）は自分のテンプレートの中に持ち、
    /// 列一覧（素の ItemsControl）は持たないので BoardView の外側のものを使う。内側を先に探す。
    /// </summary>
    private static ScrollViewer? FindScrollViewer(FrameworkElement element)
        => FindOwnScrollViewer(element, element) ?? FindAncestor(element);

    /// <summary>
    /// 自分のテンプレートの中の ScrollViewer だけを探す。入れ子の ItemsControl に入ったら
    /// そこで打ち切る。これをしないと、列一覧から探したときに中のカード一覧の
    /// ScrollViewer を掴んでしまい、列を掴んでいるのにカードが縦スクロールする。
    /// </summary>
    private static ScrollViewer? FindOwnScrollViewer(DependencyObject node, FrameworkElement root)
    {
        if (node is ScrollViewer found) return found;
        if (!ReferenceEquals(node, root) && node is ItemsControl) return null;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (FindOwnScrollViewer(VisualTreeHelper.GetChild(node, i), root) is { } child) return child;
        }
        return null;
    }

    private static ScrollViewer? FindAncestor(DependencyObject node)
    {
        for (var current = ParentOf(node); current is not null; current = ParentOf(current))
        {
            if (current is ScrollViewer found) return found;
        }
        return null;
    }
```

`RemoveDecorations` を次にする。

```csharp
    /// <summary>ドラッグが終わったときに必ず呼ぶ。</summary>
    private static void RemoveDecorations()
    {
        RemoveInsertion();
        RemoveGhost();
        StopAutoScroll();
    }
```

- [ ] **Step 2: ビルドと全テストを通す**

```
dotnet build
```

Expected: 0 警告 0 エラー

```
dotnet test tests/MoTask.Core.Tests
dotnet test tests/MoTask.Data.Tests
dotnet test tests/MoTask.App.Tests
dotnet test tests/MoTask.Mcp.Tests
```

Expected: 全緑。合計が着手前の 751 本 + Task 1 の 19 本を下回らない

- [ ] **Step 3: 手動で仕様 §7.2 のチェックリストを全部通す**

`dotnet run --project src/MoTask.App` で起動し、仕様書 §7.2 の 15 項目をすべて確認する。

- [ ] カードを同じ列の中で上下に並び替えられる。順序が保存される
- [ ] カードを別の列へ移せる。移動先の狙った位置に入る
- [ ] カードを今いる場所へ落としても保存が走らない（裁定 6）
- [ ] 列ヘッダーを掴んで列を並び替えられる。順序が保存される
- [ ] 列ヘッダーのメニューボタンを押しても列を掴まない
- [ ] 列の改名欄・WIP 入力欄をドラッグしても列を掴まない（文字選択ができる）
- [ ] カードを列ヘッダーの上へ落としても列の並びが壊れない
- [ ] ドラッグ中、挿入線が落ちる位置に出る
- [ ] ドラッグ中、掴んだ物のゴーストがカーソルに付いてくる
- [ ] ドロップ先の外へカーソルを出してもゴーストが固まらない
- [ ] Esc で中断するとゴーストも挿入線も消え、何も保存されない
- [ ] 列が画面幅に収まらないとき、カードを掴んで右端へ寄せると横にスクロールする
- [ ] カードが多い列で、掴んで下端へ寄せると縦にスクロールする
- [ ] スクロールした列でも挿入線と落ちる位置が食い違わない
- [ ] 保存に失敗したときバナーが出る（`RunGuarded` の経路が生きている）

最後の 1 項目は、`MoTask.db` を読み取り専用にしてから移動を試すと再現できる。確認後は属性を戻す。

- [ ] **Step 4: 仕様書のチェックリストに結果を書き込む**

`docs/superpowers/specs/2026-09-12-drop-third-party-oss-deps-design.md` の §7.2 のチェックボックスを、確認できた項目について `- [x]` に変える。通らなかった項目があれば、そこで止めて原因を直す。

- [ ] **Step 5: コミットする**

```bash
git add -A
git commit
```

件名: `feat(app): D&D 中の自動スクロールを足す`

- [ ] **Step 6: master へ統合する**

```bash
git checkout master
git merge --no-ff feature/drop-gong-dragdrop
```

`git rebase` と `git reset --hard` は使わない。統合後にもう一度ビルドとテストを通す。
