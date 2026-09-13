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
