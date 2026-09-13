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
