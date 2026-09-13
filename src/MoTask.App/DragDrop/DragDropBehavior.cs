using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MoTask.App.DragDrop;

/// <summary>
/// WPF 標準の D&amp;D を MoTask の IDropHandler / IDragHandler につなぐ添付ビヘイビア。
/// サードパーティの D&amp;D ライブラリを置き換えたもの。
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

    /// <summary>
    /// _origin を記録した要素。Preview イベントはトンネリングするので、押した要素の子孫で
    /// マウスが動くと OnSourceMouseMove の sender は押した要素と別物になりうる（例: 列ヘッダーで
    /// 押してカード一覧まで動かす）。sender がこの要素と一致するときだけ _origin を使う。
    /// </summary>
    private static FrameworkElement? _originSource;

    private static InsertionAdorner? _insertion;

    private static DragGhostAdorner? _ghost;

    /// <summary>端から何 dip 以内でスクロールを始めるか。</summary>
    private const double AutoScrollMargin = 24;

    /// <summary>スクロールの刻み。1 tick ごとに ScrollViewer の Line 系を 1 回呼ぶ。</summary>
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(50);

    private enum ScrollDirection { None, Left, Right, Up, Down }

    private static DispatcherTimer? _autoScrollTimer;
    private static ScrollViewer? _autoScrollViewer;
    private static ScrollDirection _autoScrollDirection = ScrollDirection.None;

    /// <summary>スクロールに合わせて挿入線を引き直す対象のドロップ先。</summary>
    private static FrameworkElement? _autoScrollTarget;

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
        element.GiveFeedback -= OnGiveFeedback;

        if (e.NewValue is not true) return;

        element.PreviewMouseLeftButtonDown += OnSourceMouseDown;
        element.PreviewMouseMove += OnSourceMouseMove;
        element.PreviewMouseLeftButtonUp += OnSourceMouseUp;
        element.GiveFeedback += OnGiveFeedback;
    }

    private static void OnSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (IsIgnored(e.OriginalSource as DependencyObject, element))
        {
            _origin = null;
            _originSource = null;
            return;
        }
        _origin = e.GetPosition(element);
        _originSource = element;
    }

    private static void OnSourceMouseUp(object sender, MouseButtonEventArgs e)
    {
        _origin = null;
        _originSource = null;
    }

    private static void OnSourceMouseMove(object sender, MouseEventArgs e)
    {
        // Preview イベントはトンネリングするので、押した要素の子孫まで動くと sender が
        // 押した要素と食い違う。_originSource と一致しないなら、この _origin は別の座標系の
        // 値なので使わない。
        if (!ReferenceEquals(sender, _originSource)) return;
        if (_origin is not { } origin) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _origin = null;
            _originSource = null;
            return;
        }

        var element = (FrameworkElement)sender;
        var now = e.GetPosition(element);
        if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _origin = null;
        _originSource = null;
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

    /// <summary>ハンドラは呼ばない。装飾を外してスクロールを止めるだけ。</summary>
    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        RemoveInsertion();
        StopAutoScroll();
    }

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
        _autoScrollTarget = element;

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

        // スクロールした分だけ項目の位置が動くので、挿入線も引き直す。DragOver は
        // カーソルが動かない限り来ないため、ここで引き直さないと線だけ取り残される。
        viewer.UpdateLayout();
        if (_autoScrollTarget is not { } target) return;
        if (!NativeCursor.GetCursorPos(out var point)) return;

        var items = target as ItemsControl;
        var cursor = target.PointFromScreen(new Point(point.X, point.Y));
        var index = InsertIndexCalculator.Calculate(RealizedContainers(items, target), cursor, OrientationOf(items));
        ShowInsertion(target, index);
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
        _autoScrollTarget = null;
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

    /// <summary>ドラッグが終わったときに必ず呼ぶ。</summary>
    private static void RemoveDecorations()
    {
        RemoveInsertion();
        RemoveGhost();
        StopAutoScroll();
    }
}
