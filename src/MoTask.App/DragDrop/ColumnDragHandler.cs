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
