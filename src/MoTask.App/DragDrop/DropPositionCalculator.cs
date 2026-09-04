using MoTask.App.ViewModels;

namespace MoTask.App.DragDrop;

/// <summary>
/// D&amp;D の挿入位置を Core が期待する position へ直す純粋な計算。UI スレッドも
/// gong-wpf-dragdrop の型も要らないので、そのまま単体テストできる。
/// </summary>
public static class DropPositionCalculator
{
    /// <summary>
    /// Gong の InsertIndex（表示カード列での挿入位置）を、Core の position
    /// （移動カードを除いた列内全カード列での挿入位置）に変換する。null は「自分の上に落とした」= 変化なし。
    /// </summary>
    public static int? ToPosition(
        IReadOnlyList<TaskCardViewModel> visible,
        IReadOnlyList<TaskCardViewModel> all,
        TaskCardViewModel moving,
        int insertIndex)
    {
        var others = all.Where(c => c.Id != moving.Id).ToList();
        if (insertIndex >= visible.Count) return others.Count;
        var anchor = visible[Math.Max(0, insertIndex)];
        if (anchor.Id == moving.Id) return null;
        return others.IndexOf(anchor);
    }

    /// <summary>
    /// 同じ列の中で、そのカードが今いる場所へ落としただけかどうか。裁定6: 何も変わらないドロップで
    /// サービスを往復させない（無駄な保存も、無駄な保存失敗バナーも出さない）。
    /// </summary>
    public static bool IsNoOp(IReadOnlyList<TaskCardViewModel> all, TaskCardViewModel moving, int position)
    {
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Id == moving.Id) return i == position;
        }
        return false;
    }

    /// <summary>列の並び替え: current から moving を抜き、InsertIndex に差し込んだ新しい順序を返す。</summary>
    public static IReadOnlyList<T> Reorder<T>(IReadOnlyList<T> current, T moving, int insertIndex) where T : class
    {
        var list = current.ToList();
        var from = list.IndexOf(moving);
        if (from < 0) return current;
        list.RemoveAt(from);
        if (insertIndex > from) insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, list.Count);
        list.Insert(insertIndex, moving);
        return list;
    }
}
