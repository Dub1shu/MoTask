# 個人 OSS 依存の解消（gong-wpf-dragdrop / NSubstitute）設計仕様

日付: 2026-09-12
状態: 設計承認待ち（実装計画は未作成）
動機: 供給経路の安全性。企業や財団の管理下にない個人・少人数 OSS への依存を減らす。

## 1. 何をするか

MoTask が使う NuGet パッケージのうち、管理主体が個人ないし少人数のコミュニティである
2 つを依存から外す。

| パッケージ | 管理主体 | 置き換え先 |
|---|---|---|
| gong-wpf-dragdrop 4.0.0 | punker76 氏（個人主導） | WPF 本体の `System.Windows.DragDrop` を直接使う自前実装 |
| NSubstitute 6.2.0 | nsubstitute プロジェクト（少人数） | 手書きのテストダブル |

残す依存とその理由は §2 に書く。

## 2. スコープ

### 含む

- `gong-wpf-dragdrop` の削除と、WPF 標準 API による D&D の自前実装
- `NSubstitute` の削除と、`MoTask.App.Tests` のテストダブルの手書き化
- `Directory.Packages.props` からの当該 2 行の削除

### 含まない

- **`FluentAssertions` の置き換え。** 7.2.2 は Xceed 社の管理下にあり Apache 2.0。
  加えて `.Should()` は 4 テストプロジェクト 67 ファイル 1777 箇所にあり、
  置き換えの費用が安全上の利得に見合わない。**残す。**
- **`xunit` / `CommunityToolkit.Mvvm` の置き換え。** どちらも .NET Foundation の
  プロジェクトで、個人 OSS ではない。**残す。**
- **D&D の仕様変更。** 何をどこへ落とせるか、落とした結果どう保存されるかは今のまま。
  この作業で利用者から見た振る舞いが変わってはいけない。
- ViewModel 層と Core 層の変更。`BoardViewModel.MoveCardAsync` /
  `ReorderColumnsAsync` / `RunGuarded` には触らない。
- 朝プラン画面・MCP・Hooks への変更。

## 3. 現状の把握

### 3.1 gong に触れている場所

| ファイル | 触れ方 |
|---|---|
| `src/MoTask.App/DragDrop/CardDropHandler.cs` | `IDropTarget` を実装 |
| `src/MoTask.App/DragDrop/ColumnDropHandler.cs` | `IDropTarget` を実装 |
| `src/MoTask.App/DragDrop/ColumnDragHandler.cs` | `DefaultDragHandler` を継承 |
| `src/MoTask.App/DragDrop/DropPositionCalculator.cs` | **gong 非依存**（注釈に名前が出るだけ） |
| `src/MoTask.App/Views/BoardView.xaml` | 名前空間 `dd` と `IsDropTarget` |
| `src/MoTask.App/Views/BoardView.xaml.cs` | `SetDropHandler` |
| `src/MoTask.App/Views/ColumnView.xaml` | 名前空間 `dd`、`IsDragSource` / `IsDropTarget` / `DragSourceIgnore` 3 箇所 / `UseDefaultDragAdorner` 2 箇所 |
| `src/MoTask.App/Views/ColumnView.xaml.cs` | `SetDropHandler` / `SetDragHandler` |
| `tests/MoTask.App.Tests/DropHandlerTests.cs` | `IDropInfo` を差し替えて 12 本のテスト |

ハンドラが実際に読み書きしている gong のメンバーは次の 9 つしかない。

- `IDropInfo`: `Data` / `TargetCollection` / `InsertIndex` / `NotHandled` / `Effects` / `DropTargetAdorner`
- `IDragInfo`: `VisualSource` / `Data` / `Effects`

`DropTargetAdorner` には常に `Insert` しか入れていない。
この表面積の小ささが、自前実装を現実的にしている。

### 3.2 NSubstitute に触れている場所

`MoTask.App.Tests` の 11 ファイル、約 280 箇所。差し替えられている型は 6 つ。

| 型 | メンバー数 | 備考 |
|---|---|---|
| `IBoardService` | 23 | 5 ファイルで使用 |
| `IMorningService` | 16 | 1 ファイル |
| `IAiJobService` | 11 | 4 ファイル |
| `IAiSettingsStore` | 2 | 1 ファイル |
| `IBoardChangeSource` | 1 | 4 ファイル |
| `IDropInfo`（gong） | 該当なし | §4 の作業で不要になる |

`MoTask.Core.Tests/Fakes/` と `MoTask.Mcp.Tests/Fakes/` には既に手書きの
テストダブルがある。`MoTask.App.Tests` だけが NSubstitute に寄っている。

### 3.3 モックライブラリを乗り換えない理由

企業や財団が管理する .NET のモックライブラリは事実上存在しない。Moq は個人管理で
SponsorLink の件があり、FakeItEasy も少人数のコミュニティ管理である。
乗り換えても管理主体の性質は変わらないので、依存そのものを無くす。

## 4. D&D の設計

### 4.1 自前の抽象

`MoTask.App.DragDrop` に、gong の該当部分と同じ形の型を置く。

```csharp
public interface IDropContext
{
    object? Data { get; }
    IEnumerable? TargetCollection { get; }
    int InsertIndex { get; }
    bool NotHandled { get; set; }
    DragDropEffects Effects { get; set; }
}

public interface IDragContext
{
    FrameworkElement? VisualSource { get; }
    object? Data { get; set; }
    DragDropEffects Effects { get; set; }
}

public interface IDropHandler
{
    void DragOver(IDropContext context);
    void Drop(IDropContext context);
}

public interface IDragHandler
{
    bool CanStartDrag(IDragContext context);
    void StartDrag(IDragContext context);
}
```

`DragDropEffects` は `System.Windows` の型なので自前では作らない。

gong の `IDropTarget` にあった `DragEnter` / `DragLeave` / `DropHint` は、
今どちらのハンドラでも中身が空か `DragOver` への転送なので、`IDropHandler` からは落とす。

### 4.2 既存ハンドラへの影響

`CardDropHandler` と `ColumnDropHandler` は `IDropTarget` を `IDropHandler` に替え、
`DragEnter` / `DragLeave` / `DropHint` の空実装と `DropTargetAdorner` への代入を消す。
**判断ロジックは 1 行も変えない。**

`ColumnDragHandler` は `DefaultDragHandler` の継承をやめ `IDragHandler` を実装する。
`CanStartDrag` と `StartDrag` の中身はそのまま。

`DropPositionCalculator` は無改変。注釈の「Gong の InsertIndex」という文言だけ直す。

### 4.3 添付ビヘイビア

`MoTask.App.DragDrop.DragDropBehavior` を新設し、次の添付プロパティを公開する。
XAML は名前空間の宣言を `urn:gong-wpf-dragdrop` から自前の
`clr-namespace:MoTask.App.DragDrop` に替えるだけで済む。

| プロパティ | 型 | 意味 |
|---|---|---|
| `IsDragSource` | bool | この要素からドラッグを始められる |
| `IsDropTarget` | bool | この要素でドロップを受ける |
| `DragSourceIgnore` | bool | この要素の上で押し始めたドラッグは開始しない |
| `DragHandler` | `IDragHandler` | 省略時は既定の実装（§4.4）を使う |
| `DropHandler` | `IDropHandler` | 必須。コードビハインドから挿す |

`DragHandler` / `DropHandler` はコードビハインドから挿す今の形を保つ。
ViewModel から D&D の型を見せないという既存の設計方針を変えないためである。

### 4.4 ドラッグの開始

`IsDragSource` が立った要素に `PreviewMouseLeftButtonDown` /
`PreviewMouseMove` / `PreviewMouseLeftButtonUp` を掛ける。

1. 押下時、押した点と `e.OriginalSource` を覚える。ただし `e.OriginalSource` から
   ドラッグ元要素まで視覚ツリーを遡る間に `DragSourceIgnore` が立った要素があれば覚えない。
   列ヘッダーのメニューボタン・改名欄・WIP 入力欄で列を掴まないためである。
2. 移動時、左ボタンが押されたままで、押下点からの距離が
   `SystemParameters.MinimumHorizontalDragDistance` /
   `MinimumVerticalDragDistance` を超えたら開始する。
3. `IDragContext` を組み、`DragHandler.CanStartDrag` が false なら何もしない。
   true なら `StartDrag` を呼び、`Data` が null でなければ `DragDrop.DoDragDrop` を呼ぶ。
4. `DoDragDrop` から戻ったら押下点を捨てる。ボタンを離したときも捨てる。

**既定のドラッグハンドラ**（`DragHandler` 省略時、カード一覧で使う）は、
押した点の下にある項目コンテナを `ItemsControl.ContainerFromElement` で求め、
その `DataContext` を `Data` に載せる。`Effects` は `Move`。
選択状態ではなく押した場所を見るので、未選択のカードを掴んでも意図どおりに動く。

**運ぶ物の渡し方。** `DataObject` には目印の文字列だけを入れ、実体は
`DragDropBehavior` の静的フィールドに置く。`DoDragDrop` から戻るときに必ず消す。
`DataObject` に ViewModel を直接入れると、ウィンドウの外へカーソルが出たときに
OLE がシリアライズを試みて失敗しうるためである。アプリは単一インスタンスなので、
静的フィールドで取り違えは起きない。

### 4.5 ドロップ先

`IsDropTarget` が立った要素に `AllowDrop = true` を立て、
`DragEnter` / `DragOver` / `DragLeave` / `Drop` を掛ける。

`IDropHandler` には `DragEnter` が無いので、`DragEnter` は
`DropHandler.DragOver` に回す。今の `CardDropHandler.DragEnter` と
`ColumnDropHandler.DragEnter` が `DragOver` への転送でしかないことに合わせる。
`DragLeave` はハンドラを呼ばず、挿入線を外して自動スクロールを止めるだけにする。

`DragOver` と `Drop` では `DropContext` を作る。

- `Data`: §4.4 の静的フィールドの中身
- `TargetCollection`: ドロップ先 `ItemsControl` の `ItemsSource`
- `InsertIndex`: §4.6 で算出

`DropHandler.DragOver` / `.Drop` を呼んだあと、
`e.Effects = context.Effects` と `e.Handled = !context.NotHandled` を書く。
これは gong と同じ扱いなので、カード一覧が受けなかったドロップが親の
列一覧まで届く今の動きがそのまま保たれる。
`CardDropHandler` の長い注釈が説明している動作である。

### 4.6 挿入位置の算出

新しい純粋関数を置く。UI スレッドも WPF の制御も要らないので単体テストできる。

```csharp
public static class InsertIndexCalculator
{
    /// containers は「実体化済みの項目コンテナの、ドロップ先要素から見た矩形」と
    /// 「その項目の ItemsSource 上の添字」の組。cursor も同じ座標系。
    public static int Calculate(
        IReadOnlyList<(int Index, Rect Bounds)> containers,
        Point cursor,
        Orientation orientation);
}
```

規則は「カーソルがどの項目の手前にあるか」。各コンテナの中線（縦並びなら上下の中央、
横並びなら左右の中央）をカーソルが越えていなければ、その項目の添字を返す。
どれも越えていなければ、最後のコンテナの添字に 1 を足した値を返す。
項目が 1 つもなければ 0。

この意味づけは gong の `InsertIndex` と同じで、
`DropPositionCalculator.ToPosition` と `.Reorder` がそのまま使える。

**仮想化への対応。** `ListBox` は既定で項目を仮想化するので、
`ItemContainerGenerator.ContainerFromIndex` は画面外の項目に対して null を返す。
実体化済みのコンテナだけを集め、そのときの本来の添字を一緒に持つ。
画面外へ落とすことはそもそもできないので、これで足りる。

### 4.7 挿入線

`AdornerLayer` に線 1 本だけを描く `InsertionAdorner` を置く。
`DragOver` のたびに挿入位置から線の座標を求め直す。
`DragLeave` と `Drop` と、ドラッグが終わったときに必ず外す。

- カード一覧（縦並び）: 挿入位置の項目の上端に水平な線
- 列一覧（横並び）: 挿入位置の項目の左端に垂直な線
- 末尾に挿す場合は最後の項目の下端 / 右端

### 4.8 ゴースト

掴んでいる要素の見た目をカーソルに追従させる。
`VisualBrush` で元要素を写した `Rectangle` を、ウィンドウ直下の `AdornerLayer` に
半透明で描く `DragGhostAdorner` を置く。

位置の更新はドラッグ元の `GiveFeedback` で行う。このイベントはドロップ先の
有無にかかわらず継続して起きるので、ドロップ先の隙間にカーソルがあっても
ゴーストが止まらない。画面座標は `user32.dll` の `GetCursorPos` を
`LibraryImport` で呼んで得る。`GiveFeedback` では `DragEventArgs` を使えないためである。

`DoDragDrop` から戻ったときに必ず外す。例外が出ても外すよう `finally` に置く。

### 4.9 自動スクロール

ドロップ先の祖先にある `ScrollViewer` を探し、カーソルが端から一定距離
（24 device-independent pixel）以内にある間だけ、`DispatcherTimer`（50 ミリ秒間隔）で
少しずつスクロールする。カーソルが端から離れたら、およびドラッグが終わったら止める。

- カード一覧: `ListBox` 内の `ScrollViewer` を縦に
- 列一覧: `BoardView` の外側の `ScrollViewer` を横に

端からの距離と 1 回あたりのスクロール量はコード内の定数にする。設定にはしない。

### 4.10 XAML の変更

| 今 | これから |
|---|---|
| `xmlns:dd="urn:gong-wpf-dragdrop"` | `xmlns:dd="clr-namespace:MoTask.App.DragDrop"` |
| `dd:DragDrop.IsDropTarget="True"` | `dd:DragDropBehavior.IsDropTarget="True"` |
| `dd:DragDrop.IsDragSource="True"` | `dd:DragDropBehavior.IsDragSource="True"` |
| `dd:DragDrop.DragSourceIgnore="True"` | `dd:DragDropBehavior.DragSourceIgnore="True"` |
| `dd:DragDrop.UseDefaultDragAdorner="True"` | 削除（ゴーストは常に出す） |

コードビハインドの `GongDragDrop.SetDropHandler` / `SetDragHandler` は
`DragDropBehavior.SetDropHandler` / `SetDragHandler` になる。
`BoardView.AttachDragDrop` と `ColumnView.AttachDragDrop` の構造は変えない。

## 5. NSubstitute 撤去の設計

`tests/MoTask.App.Tests/Fakes/` を新設し、`MoTask.Core.Tests/Fakes/` と同じ流儀で書く。

### 5.1 テストダブルの形

各インターフェースにつき 1 クラス。共通の作りは次のとおり。

- **既定の応答は成功。** `Task<Result>` を返すものは `Result.Ok()`、
  一覧を返すものは空。テストが何も言わなければ、それらしく動く。
- **差し替えたい所だけデリゲート。** `public Func<int, int, int, Task<Result>>? OnMoveTask { get; set; }`
  のような可変プロパティを置く。null なら既定の応答。
- **呼び出しは記録する。** `public List<MoveTaskCall> MoveTaskCalls { get; } = new();` に
  引数を `record` で積む。`CancellationToken` は記録しない。
  今の `Arg.Any<CancellationToken>()` がこれで不要になる。

### 5.2 検証の書き換え

| 今 | これから |
|---|---|
| `_service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(...)` | `_service.OnGetBoard = () => ...` |
| `await _service.Received(1).MoveTaskAsync(10, 2, 0, Arg.Any<CancellationToken>())` | `_service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 2, 0))` |
| `await _service.DidNotReceive().UpdateTaskAsync(...)` | `_service.UpdateTaskCalls.Should().BeEmpty()` |
| `Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 200)` | 記録した値を直接調べる |

FluentAssertions は残るので、検証の読みやすさは落ちない。

### 5.3 `IDropInfo` の差し替えは消える

`DropHandlerTests.Info(...)` は `Substitute.For<IDropInfo>()` を組み立てているが、
§4.1 の `IDropContext` は素直な実装クラスを直接 `new` できる。
テストダブルは不要になる。

## 6. 作業順序

**D&D を先、NSubstitute を後。** 2 本のブランチに分ける。

逆順にすると、`DropHandlerTests` のために gong の `IDropInfo`
（20 以上のメンバーを持つ）の手書きテストダブルを作る羽目になり、
その直後に捨てることになる。

### 1 本目: gong の撤去

1. `IDropContext` / `IDragContext` / `IDropHandler` / `IDragHandler` を追加
2. `InsertIndexCalculator` を追加（テスト先行）
3. 既存 3 ハンドラを新しい型に載せ替え。`DropHandlerTests` を実装クラス直接生成に直す
4. `DragDropBehavior` を追加（開始・ドロップ受け・伝播）
5. `InsertionAdorner` / `DragGhostAdorner` / 自動スクロールを追加
6. XAML とコードビハインドを差し替え
7. `Directory.Packages.props` と `MoTask.App.csproj` から `gong-wpf-dragdrop` を削除
8. 手動確認（§7.2）

### 2 本目: NSubstitute の撤去

1. `Fakes/` に 5 つのテストダブルを追加
2. 11 ファイルを 1 ファイルずつ移す。1 ファイル移すごとにテストを通す
3. `Directory.Packages.props` と `MoTask.App.Tests.csproj` から `NSubstitute` を削除

## 7. 検証

### 7.1 自動

- `dotnet build` が通り、コンパイラ警告が今より増えない
- `dotnet test` が全緑。テストの本数が着手前を下回らない
- `gong` / `GongSolutions` / `NSubstitute` を `src/` `tests/` `Directory.Packages.props`
  `MoTask.sln` から検索して、`obj/` を除いて何も出ない

着手前の実測値（2026-09-12）は次のとおり。MoTask.App.Tests だけは
アプリが起動していると出力 DLL がロックされて測れないので、
着手時に MoTask.exe と Visual Studio を閉じて基準を取り直すこと。

| プロジェクト | テスト数 |
|---|---|
| MoTask.Core.Tests | 294 |
| MoTask.Data.Tests | 41 |
| MoTask.Mcp.Tests | 21 |
| MoTask.App.Tests | 未計測 |

### 7.2 手動チェックリスト（1 本目のあと）

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

### 7.3 手動チェックリスト（2 本目のあと）

自動テストのみで足りる。振る舞いは変わらない。

## 8. 残るリスク

- **自前の D&D は gong より作り込みが浅い。** 複数選択のドラッグ、
  ドラッグ中のキー修飾によるコピー、ドロップ先の入れ子など、gong が持つ機能は
  再現しない。MoTask はどれも使っていないので今は困らないが、
  将来必要になったら自分で足すことになる。
- **`GetCursorPos` の P/Invoke が 1 箇所入る。** WPF の `GiveFeedback` から
  カーソル位置を取る手段が他にないためである。`LibraryImport` で書き、
  用途をその場に注釈する。
- **仮想化されたリストの端の挙動。** §4.6 の方針は「見えている所にしか落とせない」
  前提に立つ。自動スクロールがあるので実用上は届くが、
  極端に長い列では gong と微妙に違う位置に入る余地がある。
