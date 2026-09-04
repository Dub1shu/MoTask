using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class TransactionTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task SaveChanges_WhenHistoryInsertFails_RollsBackTaskChange_AndClearsTracker()
    {
        int taskId;
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
            var column = await ctx.Columns.FirstAsync();
            var now = DateTime.UtcNow;
            var task = new TaskItem { Title = "元", ColumnId = column.Id, CreatedAt = now, UpdatedAt = now };
            ctx.Tasks.Add(task);
            await ctx.SaveChangesAsync();
            taskId = task.Id;
        }

        await using (var ctx = _db.CreateContext())
        {
            var boards = new BoardRepository(ctx);
            var history = new HistoryRepository(ctx);
            var uow = new EfUnitOfWork(ctx);

            var task = (await boards.GetTaskAsync(taskId))!;
            task.Title = "変更後";
            // 存在しないタスクを指す履歴 → 外部キー違反で SaveChanges が失敗する
            history.Add(new HistoryEntry { TaskId = 999_999, At = DateTime.UtcNow, Kind = HistoryKind.Edited, Detail = "{}" });

            var act = () => uow.SaveChangesAsync();

            await act.Should().ThrowAsync<PersistenceException>();
            ctx.ChangeTracker.Entries().Should().BeEmpty("失敗後は未保存の変更を捨てる");
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.Tasks.SingleAsync(t => t.Id == taskId)).Title.Should().Be("元");
            (await ctx.History.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task HistoryRepository_ReturnsNewestFirst()
    {
        await using var ctx = _db.CreateContext();
        await new DatabaseInitializer(ctx).InitializeAsync();
        var column = await ctx.Columns.FirstAsync();
        var task = new TaskItem { Title = "t", ColumnId = column.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.Tasks.Add(task);
        var t0 = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        ctx.History.Add(new HistoryEntry { Task = task, At = t0, Kind = HistoryKind.Created });
        ctx.History.Add(new HistoryEntry { Task = task, At = t0.AddMinutes(1), Kind = HistoryKind.Moved });
        await ctx.SaveChangesAsync();

        var list = await new HistoryRepository(ctx).GetForTaskAsync(task.Id);

        list.Select(h => h.Kind).Should().Equal(HistoryKind.Moved, HistoryKind.Created);
    }

    public void Dispose() => _db.Dispose();
}
