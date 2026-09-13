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
