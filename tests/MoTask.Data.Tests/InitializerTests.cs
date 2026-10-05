using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Data.Tests;

public class InitializerTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public async Task Initialize_OnEmptyDb_SeedsDefaultBoardAndFiveColumns()
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
                ("今日中", ColumnRole.Today, 1),
                ("進行中", ColumnRole.Active, 2),
                ("確認待ち", ColumnRole.Review, 3),
                ("完了", ColumnRole.Done, 4));
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
        (await ctx.Columns.CountAsync()).Should().Be(5);
    }

    /// <summary>
    /// 起動を速くするため、当てるマイグレーションが無ければ MigrateAsync を呼ばない
    /// （呼ぶと移行ロックの取得・解除とモデル差分の確認で毎回 0.5 秒ほどかかる）。
    /// 移行ロックのテーブルに触れていないことで確かめる。
    /// </summary>
    [Fact]
    public async Task Initialize_OnUpToDateDb_DoesNotRunMigrate()
    {
        await using (var ctx = _db.CreateContext())
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
        }

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<MoTaskDbContext>()
            .UseSqlite(DbPaths.ConnectionString(_db.Path))
            .AddInterceptors(recorder)
            .Options;
        await using (var ctx = new MoTaskDbContext(options))
        {
            await new DatabaseInitializer(ctx).InitializeAsync();
        }

        recorder.Commands.Should().NotBeEmpty();
        recorder.Commands.Should().NotContain(c => c.Contains("__EFMigrationsLock"));
    }

    private sealed class CommandRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public void Dispose() => _db.Dispose();
}
