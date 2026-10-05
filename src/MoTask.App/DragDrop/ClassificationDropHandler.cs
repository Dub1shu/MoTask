using System.Windows;
using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>
/// 管理ダイアログの行の並び替え。プロジェクトとラベルの一覧に同じものを挿す。
/// 行は「生きている行 → アーカイブ済み」の順に並んでいるので、生きている行の範囲の中でだけ動かす。
/// </summary>
public sealed class ClassificationDropHandler : IDropHandler
{
    private readonly ManageClassificationsViewModel _manage;

    public ClassificationDropHandler(ManageClassificationsViewModel manage)
    {
        _manage = manage;
    }

    public void DragOver(IDropContext context)
    {
        if (LiveRows(context) is not { } live)
        {
            context.NotHandled = true;
            return;
        }
        // アーカイブ済みの行の間には挿入線を出さない
        context.InsertIndex = Math.Min(context.InsertIndex, live.Count);
        context.Effects = DragDropEffects.Move;
    }

    public void Drop(IDropContext context)
    {
        if (context.Data is not ClassificationRow moving || LiveRows(context) is not { } live)
        {
            context.NotHandled = true;
            return;
        }

        var order = DropPositionCalculator.Reorder(live, moving, Math.Min(context.InsertIndex, live.Count));
        // 列のドロップと同じく、並びが変わらないドロップは保存を往復させない
        if (order.SequenceEqual(live))
        {
            context.NotHandled = true;
            return;
        }

        context.Effects = DragDropEffects.Move;
        _manage.Reorder(order);
    }

    /// <summary>運んできた行と同じ一覧の上でだけ受ける（プロジェクトをラベルの一覧へは移せない）。アーカイブ済みの行は運ばない。</summary>
    private static IReadOnlyList<ClassificationRow>? LiveRows(IDropContext context)
        => context.Data is ClassificationRow { IsArchived: false } row
           && context.TargetCollection is IEnumerable<ClassificationRow> rows
           && rows.Contains(row)
            ? rows.Where(r => !r.IsArchived).ToList()
            : null;
}
