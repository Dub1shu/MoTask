using System.Collections.ObjectModel;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>列の並び替え。列ヘッダーから始めたドラッグを列の ItemsControl で受ける。</summary>
public sealed class ColumnDropHandler : IDropTarget
{
    private readonly BoardViewModel _board;

    public ColumnDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropInfo dropInfo)
    {
        // 列以外（カードのドラッグ）はここでは受けない。握らず親へ返す。
        if (!CanAccept(dropInfo))
        {
            dropInfo.NotHandled = true;
            return;
        }
        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        dropInfo.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not ColumnViewModel moving) return;
        if (dropInfo.TargetCollection is not ObservableCollection<ColumnViewModel>) return;

        var current = _board.Columns.ToList();
        var order = DropPositionCalculator.Reorder(current, moving, dropInfo.InsertIndex);
        // 裁定6 と同じ理由: 並びが変わらないドロップは保存を往復させない。
        if (order.SequenceEqual(current)) return;

        _board.RunGuarded(() => _board.ReorderColumnsAsync(order));
    }

    public void DragEnter(IDropInfo dropInfo) => DragOver(dropInfo);

    public void DragLeave(IDropInfo dropInfo)
    {
    }

    public void DropHint(IDropHintInfo dropHintInfo)
    {
    }

    private static bool CanAccept(IDropInfo dropInfo)
        => dropInfo.Data is ColumnViewModel && dropInfo.TargetCollection is ObservableCollection<ColumnViewModel>;
}
