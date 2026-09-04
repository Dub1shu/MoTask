using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class RepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task GetBoard_ReturnsColumnsByOrder_AndTasksByPosition_IncludingDeleted()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "並び確認" };
            var second = new Column { Name = "二番目", Order = 1 };
            var first = new Column { Name = "一番目", Order = 0 };
            board.Columns.Add(second);
            board.Columns.Add(first);
            var now = DateTime.UtcNow;
            first.Tasks.Add(new TaskItem { Title = "p2", Position = 2, CreatedAt = now, UpdatedAt = now });
            first.Tasks.Add(new TaskItem { Title = "p0", Position = 0, CreatedAt = now, UpdatedAt = now });
            first.Tasks.Add(new TaskItem { Title = "p1-deleted", Position = 1, CreatedAt = now, UpdatedAt = now, DeletedAt = now });
            ctx.Boards.Add(board);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new BoardRepository(ctx);
            var board = (await repo.GetBoardAsync())!;
            board.Columns.Select(c => c.Name).Should().Equal("一番目", "二番目");
            board.Columns[0].Tasks.Select(t => t.Title).Should().Equal("p0", "p1-deleted", "p2");
        }
    }

    [Fact]
    public async Task BoardService_OverSqlite_CreatesAndMovesTasks_PersistingHistory()
    {
        await using var services = new ServiceCollection()
            .AddMoTaskData(_db.Path)
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBoardService, BoardService>()
            .BuildServiceProvider();

        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        var service = services.GetRequiredService<IBoardService>();
        var board = (await service.GetBoardAsync()).Value!;
        var backlog = board.Columns.Single(c => c.Role == ColumnRole.Backlog);
        var done = board.Columns.Single(c => c.Role == ColumnRole.Done);

        var created = await service.CreateTaskAsync(backlog.Id, "永続化テスト");
        created.IsSuccess.Should().BeTrue(created.Error);
        var moved = await service.MoveTaskAsync(created.Value!.Id, done.Id, 0);
        moved.IsSuccess.Should().BeTrue(moved.Error);

        await using var ctx = _db.CreateContext();
        var task = await ctx.Tasks.SingleAsync();
        task.ColumnId.Should().Be(done.Id);
        task.CompletedAt.Should().NotBeNull();
        (await ctx.History.Where(h => h.TaskId == task.Id).OrderBy(h => h.Id).Select(h => h.Kind).ToListAsync())
            .Should().Equal(HistoryKind.Created, HistoryKind.Moved);
    }

    [Fact]
    public async Task RemoveColumn_ThenSaveChanges_DeletesColumnFromDatabase()
    {
        int columnId;
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
            columnId = (await ctx.Columns.SingleAsync(c => c.Role == ColumnRole.Backlog)).Id;
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new BoardRepository(ctx);
            var uow = new EfUnitOfWork(ctx);
            var column = (await repo.GetColumnAsync(columnId))!;

            repo.RemoveColumn(column);
            await uow.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.Columns.AnyAsync(c => c.Id == columnId)).Should().BeFalse();
        }
    }

    public void Dispose() => _db.Dispose();
}
