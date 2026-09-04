using MoTask.Core.Abstractions;
using MoTask.Core.Model;

namespace MoTask.App.Tests;

public static class TestBoards
{
    public static Project ProjectA() => new() { Id = 100, Name = "顧客A対応" };
    public static Label Urgent() => new() { Id = 200, Name = "至急", Color = "accent-500" };

    /// <summary>未着手(1): 10,11 / 進行中(2, WIP 1): 12 / 完了(3): なし</summary>
    public static Board Sample(Label? urgent = null)
    {
        urgent ??= Urgent();
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var backlog = new Column { Id = 1, BoardId = 1, Name = "未着手", Order = 0, Role = ColumnRole.Backlog };
        var active = new Column { Id = 2, BoardId = 1, Name = "進行中", Order = 1, Role = ColumnRole.Active, WipLimit = 1 };
        var done = new Column { Id = 3, BoardId = 1, Name = "完了", Order = 2, Role = ColumnRole.Done };
        backlog.Tasks.Add(new TaskItem
        {
            Id = 10, Title = "請求先情報を更新する", ColumnId = 1, Position = 0, ProjectId = 100,
            DueDate = new DateOnly(2026, 9, 8), CreatedAt = t, UpdatedAt = t, Labels = { urgent },
        });
        backlog.Tasks.Add(new TaskItem { Id = 11, Title = "求人票の文面を見直す", ColumnId = 1, Position = 1, CreatedAt = t, UpdatedAt = t });
        active.Tasks.Add(new TaskItem { Id = 12, Title = "週次レポートを作成する", ColumnId = 2, Position = 0, CreatedAt = t, UpdatedAt = t });
        var board = new Board { Id = 1, Name = "テスト" };
        board.Columns.AddRange(new[] { backlog, active, done });
        return board;
    }

    /// <summary>
    /// 未着手(1): 10, 11(削除済み), 13, 14 / 進行中(2, WIP 1): 12 / 完了(3): なし。
    /// 削除済みが列の途中に残っている状態（削除の後に別のカードを動かすと出来る）。
    /// 既定のフィルタでは削除済みが隠れるので、表示 3 枚 / AllCards 4 枚になる。
    /// </summary>
    public static Board WithDeletedCard()
    {
        var board = Sample();
        SeedDeletedInTheMiddle(board.Columns[0]);
        return board;
    }

    /// <summary><see cref="Sample"/> の未着手列を 10 / 11(削除済み) / 13 / 14 にする。</summary>
    public static Column SeedDeletedInTheMiddle(Column backlog)
    {
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        backlog.Tasks.Single(x => x.Id == 11).DeletedAt = t.AddDays(1);
        backlog.Tasks.Add(new TaskItem { Id = 13, Title = "見積書を送る", ColumnId = 1, Position = 2, CreatedAt = t, UpdatedAt = t });
        backlog.Tasks.Add(new TaskItem { Id = 14, Title = "議事録をまとめる", ColumnId = 1, Position = 3, CreatedAt = t, UpdatedAt = t });
        return backlog;
    }

    // ---- BoardService の副作用をそのまま写したヘルパー ----
    // IBoardService の差し替えは、実サービスと同じようにモデルを書き換えないと
    // 「本番では起こらない並び」をテストが前提にしてしまう（それが F1 / F2 を隠していた）。

    /// <summary>
    /// <c>BoardService.MoveTaskAsync</c> と同じ副作用。移動先の Tasks コレクションへは
    /// <b>末尾に足す</b>（＝コレクション順は Position 順と一致しない）。
    /// </summary>
    public static void Move(Column source, Column target, int taskId, int position)
    {
        var task = source.Tasks.Single(t => t.Id == taskId);
        var columnChanged = !ReferenceEquals(source, target);
        var sourceTasks = InOrder(source).Where(t => t.Id != taskId).ToList();
        var targetTasks = columnChanged ? InOrder(target) : sourceTasks;
        targetTasks.Insert(Math.Clamp(position, 0, targetTasks.Count), task);
        if (columnChanged)
        {
            source.Tasks.Remove(task);
            target.Tasks.Add(task);
            task.ColumnId = target.Id;
            Renumber(sourceTasks);
        }
        Renumber(targetTasks);
    }

    /// <summary><c>BoardService.DeleteTaskAsync</c> と同じ副作用（論理削除＋列の再採番）。</summary>
    public static void SoftDelete(Column column, int taskId, DateTime at)
    {
        column.Tasks.Single(t => t.Id == taskId).DeletedAt = at;
        RenumberColumn(column);
    }

    /// <summary><c>BoardService.RestoreTaskAsync</c> と同じ副作用（復元＋未削除分の末尾へ移動）。</summary>
    public static void Restore(Column column, int taskId)
    {
        var task = column.Tasks.Single(t => t.Id == taskId);
        task.DeletedAt = null;
        RenumberColumn(column, task);
    }

    /// <summary>未削除を Position 順に 0 から詰め、削除済みはその後ろへ回す。</summary>
    private static void RenumberColumn(Column column, TaskItem? moveToEnd = null)
    {
        var ordered = InOrder(column);
        var renumbered = ordered.Where(t => !t.IsDeleted && !ReferenceEquals(t, moveToEnd)).ToList();
        if (moveToEnd is { IsDeleted: false }) renumbered.Add(moveToEnd);
        renumbered.AddRange(ordered.Where(t => t.IsDeleted));
        Renumber(renumbered);
    }

    private static List<TaskItem> InOrder(Column column)
        => column.Tasks.OrderBy(t => t.Position).ThenBy(t => t.Id).ToList();

    private static void Renumber(List<TaskItem> tasks)
    {
        for (var i = 0; i < tasks.Count; i++) tasks[i].Position = i;
    }
}

public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 9, 4);
}
