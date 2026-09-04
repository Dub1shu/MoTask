using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Data.Tests;

public class MigrationTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task Migrate_OnEmptyFile_CreatesSchema()
    {
        await using var ctx = _db.CreateContext();

        await ctx.Database.MigrateAsync();

        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        tables.Should().Contain(new[] { "Boards", "Columns", "Tasks", "Projects", "Labels", "TaskLabels", "History" });
    }

    [Fact]
    public async Task Model_RoundTrips_DateOnlyAndEnumsAndLabels()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "b" };
            var column = new Column { Name = "c", Role = ColumnRole.Review, Order = 0 };
            board.Columns.Add(column);
            var label = new Label { Name = "至急", Color = "accent-500" };
            var task = new TaskItem
            {
                Title = "t", DueDate = new DateOnly(2026, 9, 10),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            task.Labels.Add(label);
            column.Tasks.Add(task);
            ctx.Boards.Add(board);
            ctx.History.Add(new HistoryEntry { Task = task, At = DateTime.UtcNow, Kind = HistoryKind.Created });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var task = await ctx.Tasks.Include(t => t.Labels).SingleAsync();
            task.DueDate.Should().Be(new DateOnly(2026, 9, 10));
            task.Labels.Should().ContainSingle().Which.Name.Should().Be("至急");
            (await ctx.Columns.SingleAsync()).Role.Should().Be(ColumnRole.Review);
            var history = await ctx.History.SingleAsync();
            history.TaskId.Should().Be(task.Id);
            history.Kind.Should().Be(HistoryKind.Created);
            history.Detail.Should().BeEmpty();
        }
    }

    [Fact]
    public void DbPaths_PointToLocalAppData()
    {
        DbPaths.DefaultDirectory.Should().EndWith("MoTask");
        DbPaths.DefaultDatabase.Should().EndWith(Path.Combine("MoTask", "motask.db"));
        DbPaths.ConnectionString(@"C:\x\y.db").Should().Contain("y.db");
    }

    public void Dispose() => _db.Dispose();
}
