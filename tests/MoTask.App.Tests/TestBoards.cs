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
}

public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 9, 4);
}
