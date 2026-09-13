using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

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
