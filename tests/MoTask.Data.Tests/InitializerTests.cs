using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Data.Tests;

public class InitializerTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task Initialize_OnEmptyDb_SeedsDefaultBoardAndFourColumns()
    {
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
            ctx.ChangeTracker.Entries().Should().BeEmpty("投入後に追跡をクリアする");
        }

        await using (var ctx = _db.CreateContext())
        {
            var board = await ctx.Boards.Include(b => b.Columns.OrderBy(c => c.Order)).SingleAsync();
            board.Name.Should().Be(DefaultBoard.Name);
            board.Columns.Select(c => (c.Name, c.Role, c.Order)).Should().Equal(
                ("未着手", ColumnRole.Backlog, 0),
                ("進行中", ColumnRole.Active, 1),
                ("確認待ち", ColumnRole.Review, 2),
                ("完了", ColumnRole.Done, 3));
            board.Columns.Should().OnlyContain(c => c.WipLimit == null);
        }
    }

    [Fact]
    public async Task Initialize_Twice_DoesNotDuplicateBoard()
    {
        await using var ctx = _db.CreateContext();
        var initializer = new DatabaseInitializer(ctx);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        (await ctx.Boards.CountAsync()).Should().Be(1);
        (await ctx.Columns.CountAsync()).Should().Be(4);
    }

    public void Dispose() => _db.Dispose();
}
