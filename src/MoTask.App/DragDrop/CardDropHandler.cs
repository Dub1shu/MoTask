using System.Collections.ObjectModel;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>カードの列内並び替えと列間移動。ドロップ時に MoveTask を1回だけ呼ぶ。</summary>
public sealed class CardDropHandler : IDropTarget
{
    private readonly BoardViewModel _board;

    public CardDropHandler(BoardViewModel board)
    {
        _board = board;
    }

    public void DragOver(IDropInfo dropInfo)
    {
        // カード以外（列ヘッダーのドラッグ）は受けない。NotHandled を立てて親の
        // ItemsControl（列の並び替え）まで通す。ここで握ると列をどこにも落とせなくなる。
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
        if (dropInfo.Data is not TaskCardViewModel card) return;
        if (dropInfo.TargetCollection is not ObservableCollection<TaskCardViewModel> cards) return;
        var target = _board.Columns.FirstOrDefault(c => ReferenceEquals(c.Cards, cards));
        if (target is null) return;

        var position = DropPositionCalculator.ToPosition(target.Cards, target.AllCards, card, dropInfo.InsertIndex);
        if (position is null) return;

        // 裁定6: 同じ列の今の位置へ落としただけなら、保存を往復させない。
        var source = _board.Columns.FirstOrDefault(c => c.AllCards.Contains(card));
        if (ReferenceEquals(source, target) && DropPositionCalculator.IsNoOp(target.AllCards, card, position.Value)) return;

        // Drop は Task を返せないので board 側に観測させる。discard にすると保存の失敗が消える。
        _board.RunGuarded(() => _board.MoveCardAsync(card, target, position.Value));
    }

    public void DragEnter(IDropInfo dropInfo) => DragOver(dropInfo);

    public void DragLeave(IDropInfo dropInfo)
    {
    }

    public void DropHint(IDropHintInfo dropHintInfo)
    {
    }

    private static bool CanAccept(IDropInfo dropInfo)
        => dropInfo.Data is TaskCardViewModel && dropInfo.TargetCollection is ObservableCollection<TaskCardViewModel>;
}
