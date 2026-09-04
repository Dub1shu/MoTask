using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>
/// 列ヘッダー（ItemsControl ではない要素）からのドラッグ。既定のハンドラは ItemsControl の
/// 選択項目から Data を組み立てるので、ここでは要素の DataContext から列を直接載せる。
/// </summary>
public sealed class ColumnDragHandler : DefaultDragHandler
{
    public override bool CanStartDrag(IDragInfo dragInfo)
        => (dragInfo.VisualSource as FrameworkElement)?.DataContext is ColumnViewModel;

    public override void StartDrag(IDragInfo dragInfo)
    {
        var column = (dragInfo.VisualSource as FrameworkElement)?.DataContext as ColumnViewModel;
        dragInfo.Data = column;
        dragInfo.Effects = column is null ? DragDropEffects.None : DragDropEffects.Move;
    }
}
