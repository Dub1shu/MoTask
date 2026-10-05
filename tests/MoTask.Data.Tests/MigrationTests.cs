using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
        tables.Should().Contain(new[]
        {
            "Boards", "Columns", "Tasks", "Projects", "Labels", "TaskLabels", "History",
            "AiJobs", "PlanningRuns", "TriageCandidates",
        });
    }

    /// <summary>
    /// 列挙は文字列で保存されている。廃止した名前の行が残っていると読み出しで例外になるので、
    /// TerminalAiRework が寄せ直すこと（仕様 §9）。
    /// </summary>
    [Fact]
    public async Task Migrate_RewritesRowsSavedUnderRetiredEnumNames()
    {
        await using (var old = _db.CreateContext())
        {
            // 作り替え前（AddAiJobFolder まで）の DB を作る
            await old.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("20260905135641_AddAiJobFolder");
            // DatabaseInitializer は最新まで当ててしまうので、盤面は自前で 1 枚だけ作る
            var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            var board = new Board { Name = "b" };
            var column = new Column { Name = "未着手", Role = ColumnRole.Backlog, Order = 0 };
            board.Columns.Add(column);
            old.Boards.Add(board);
            await old.SaveChangesAsync();
            old.Tasks.Add(new TaskItem { Id = 1, Title = "t", ColumnId = column.Id, CreatedAt = now, UpdatedAt = now });
            await old.SaveChangesAsync();
            // Running / Pending は「作り替え前に MoTask が落ちて、復旧処理を経ないまま残った行」を
            // 再現する（仕様 §9・移行 SQL 参照）。JobFolder は前段のマイグレーションが埋める '' のまま。
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO AiJobs (TaskId, Kind, Status, SessionId, Instruction, WorkingDirectory, JobFolder, StartedAt) " +
                "VALUES (1, 'Execute', 'Suspended', '00000000-0000-0000-0000-000000000001', 'i', 'C:\\w', '', '2026-09-05 00:00:00'), " +
                "(1, 'Execute', 'AwaitingApproval', '00000000-0000-0000-0000-000000000002', 'i', 'C:\\w', '', '2026-09-05 00:00:00'), " +
                "(1, 'Execute', 'Running', '00000000-0000-0000-0000-000000000003', 'i', 'C:\\w', '', '2026-09-05 00:00:00'), " +
                "(1, 'Execute', 'Pending', '00000000-0000-0000-0000-000000000004', 'i', 'C:\\w', '', '2026-09-05 00:00:00')");
        }

        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();

        var jobs = await ctx.Set<AiJob>().OrderBy(j => j.Id).ToListAsync();
        jobs.Select(j => j.Status).Should().Equal(
            AiJobStatus.Cancelled, AiJobStatus.Cancelled, AiJobStatus.Cancelled, AiJobStatus.Cancelled);
        jobs.Should().OnlyContain(j => j.EndedAt != null, "追跡をやめた時刻を StartedAt で埋める");
    }

    [Fact]
    public async Task Migrate_LeavesNoAiPermissionRulesTable()
    {
        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();

        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();

        tables.Should().NotContain("AiPermissionRules");
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

    [Fact]
    public async Task Project_WorkingDirectory_RoundTrips()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            ctx.Projects.Add(new Project { Name = "p", WorkingDirectory = @"C:\work\p" });
            ctx.Projects.Add(new Project { Name = "q" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.Projects.SingleAsync(p => p.Name == "p")).WorkingDirectory.Should().Be(@"C:\work\p");
            (await ctx.Projects.SingleAsync(p => p.Name == "q")).WorkingDirectory.Should().BeNull();
        }
    }

    [Fact]
    public async Task Project_Color_RoundTrips()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            ctx.Projects.Add(new Project { Name = "p", Color = "green-600" });
            ctx.Projects.Add(new Project { Name = "q" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.Projects.SingleAsync(p => p.Name == "p")).Color.Should().Be("green-600");
            (await ctx.Projects.SingleAsync(p => p.Name == "q")).Color.Should().BeNull();
        }
    }

    [Fact]
    public void DbPaths_SettingsSitNextToTheDatabase()
    {
        DbPaths.DefaultSettings.Should().EndWith(Path.Combine("MoTask", "settings.json"));
    }

    [Fact]
    public async Task AiJob_JobFolder_RoundTrips()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "b" };
            var backlog = new Column { Name = "c", Role = ColumnRole.Backlog, Order = 0 };
            board.Columns.Add(backlog);
            ctx.Boards.Add(board);
            await ctx.SaveChangesAsync();
            var now = DateTime.UtcNow;
            var task = new TaskItem { Title = "t", ColumnId = backlog.Id, CreatedAt = now, UpdatedAt = now };
            ctx.Tasks.Add(task);
            await ctx.SaveChangesAsync();
            ctx.AiJobs.Add(new AiJob
            {
                TaskId = task.Id, Kind = AiJobKind.Execute, Status = AiJobStatus.Running,
                SessionId = Guid.NewGuid(), JobFolder = @"C:\work\jobs\0001-t",
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.AiJobs.SingleAsync()).JobFolder.Should().Be(@"C:\work\jobs\0001-t");
        }
    }

    [Fact]
    public async Task AiJob_ProcessedLines_RoundTrips()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var board = new Board { Name = "b" };
            var backlog = new Column { Name = "c", Role = ColumnRole.Backlog, Order = 0 };
            board.Columns.Add(backlog);
            ctx.Boards.Add(board);
            await ctx.SaveChangesAsync();
            var now = DateTime.UtcNow;
            var task = new TaskItem { Title = "t", ColumnId = backlog.Id, CreatedAt = now, UpdatedAt = now };
            ctx.Tasks.Add(task);
            await ctx.SaveChangesAsync();
            ctx.AiJobs.Add(new AiJob
            {
                TaskId = task.Id, Kind = AiJobKind.Execute, Status = AiJobStatus.Running,
                SessionId = Guid.NewGuid(), JobFolder = @"C:\work\jobs\0001-t",
                ProcessedLines = 7,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.AiJobs.SingleAsync()).ProcessedLines.Should().Be(7);
        }
    }

    [Fact]
    public async Task Migrate_DropsTheAiJobEventsTable()
    {
        await using var db = _db.CreateContext();
        await db.Database.MigrateAsync();

        var names = await db.Database
            .SqlQuery<string>($"select name AS Value from sqlite_master where type = 'table'")
            .ToListAsync();

        names.Should().NotContain("AiJobEvents");
    }

    /// <summary>
    /// DropAiJobEvents はテーブルを落とす前に件数を AiJob.ProcessedLines へ移す。ここを飛ばすと、
    /// 移行時に進行中だったジョブが events.jsonl を先頭から読み直し、ターン数が二重に増える（仕様 §9）。
    /// </summary>
    [Fact]
    public async Task Migrate_SeedsProcessedLinesFromTheDroppedEventsTable()
    {
        await using (var old = _db.CreateContext())
        {
            // 落とす直前（AiJobProcessedLines まで）の DB を作る
            await old.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("20260906142042_AiJobProcessedLines");
            var board = new Board { Name = "b" };
            var column = new Column { Name = "未着手", Role = ColumnRole.Backlog, Order = 0 };
            board.Columns.Add(column);
            old.Boards.Add(board);
            await old.SaveChangesAsync();
            old.Tasks.Add(new TaskItem { Id = 1, Title = "t", ColumnId = column.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await old.SaveChangesAsync();
            // 移行前に進行中だったジョブ（復旧処理を経ないまま ProcessedLines = 0 で残っている）を再現する
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO AiJobs (TaskId, Kind, Status, SessionId, Instruction, WorkingDirectory, JobFolder, StartedAt) " +
                "VALUES (1, 'Execute', 'Running', '00000000-0000-0000-0000-000000000001', 'i', 'C:\\w', '', '2026-09-06 00:00:00')");
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO AiJobEvents (At, JobId, Kind, Payload, Seq, ToolName) VALUES " +
                "('2026-09-06 00:00:00', 1, 'ToolUse', '{{}}', 1, 'Read'), " +
                "('2026-09-06 00:00:01', 1, 'ToolUse', '{{}}', 2, 'Bash'), " +
                "('2026-09-06 00:00:02', 1, 'TurnEnded', '{{}}', 3, NULL)");
        }

        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();

        var job = await ctx.Set<AiJob>().SingleAsync();
        job.ProcessedLines.Should().Be(3, "移行前に AiJobEvents にあった行数がそのまま引き継がれる");
    }

    /// <summary>既存の行は Order = 0 のまま。並べ方の規則で今までの名前順が保たれる（仕様 §3）。</summary>
    [Fact]
    public async Task Migration_LeavesExistingRowsAtZero_SoTheyKeepNameOrder()
    {
        await using (var old = _db.CreateContext())
        {
            await old.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("20261004122914_AddProjectColor");
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO Projects (Name, Archived) VALUES ('B', 0), ('A', 0); " +
                "INSERT INTO Labels (Name, Color, Archived) VALUES ('y', 'accent-300', 0), ('x', 'accent-300', 0)");
        }

        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var repo = new MoTask.Data.Repositories.BoardRepository(ctx);
            var projects = await repo.GetProjectsAsync();
            projects.Select(p => p.Order).Should().AllBeEquivalentTo(0);
            projects.Select(p => p.Name).Should().Equal("A", "B");
            (await repo.GetLabelsAsync()).Select(l => l.Name).Should().Equal("x", "y");
        }
    }

    /// <summary>AddTodayColumn の 1 つ前。ここまで当てた DB に盤面を作ってから最新まで当てる。</summary>
    private const string BeforeToday = "20261004122914_AddProjectColor";

    private async Task SeedBeforeTodayAsync(params (string Name, ColumnRole Role)[] columns)
    {
        await using var old = _db.CreateContext();
        await old.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(BeforeToday);
        var board = new Board { Name = "b" };
        for (var i = 0; i < columns.Length; i++)
            board.Columns.Add(new Column { Name = columns[i].Name, Role = columns[i].Role, Order = i });
        old.Boards.Add(board);
        await old.SaveChangesAsync();
    }

    private async Task<List<(string, ColumnRole, int)>> ColumnsAfterMigrateAsync()
    {
        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();
        return (await ctx.Columns.OrderBy(c => c.Order).ToListAsync())
            .Select(c => (c.Name, c.Role, c.Order)).ToList();
    }

    /// <summary>既存の DB には今日中の列を 1 度だけ、最初の未着手の右に入れる（仕様 2026-10-05-today-column §3.3）。</summary>
    [Fact]
    public async Task Migrate_InsertsTheTodayColumnRightAfterTheBacklog()
    {
        await SeedBeforeTodayAsync(
            ("未着手", ColumnRole.Backlog), ("進行中", ColumnRole.Active),
            ("確認待ち", ColumnRole.Review), ("完了", ColumnRole.Done));

        (await ColumnsAfterMigrateAsync()).Should().Equal(
            ("未着手", ColumnRole.Backlog, 0),
            ("今日中", ColumnRole.Today, 1),
            ("進行中", ColumnRole.Active, 2),
            ("確認待ち", ColumnRole.Review, 3),
            ("完了", ColumnRole.Done, 4));
    }

    [Fact]
    public async Task Migrate_LeavesABoardThatAlreadyHasATodayColumn()
    {
        await SeedBeforeTodayAsync(
            ("未着手", ColumnRole.Backlog), ("今日やる", ColumnRole.Today), ("完了", ColumnRole.Done));

        (await ColumnsAfterMigrateAsync()).Should().Equal(
            ("未着手", ColumnRole.Backlog, 0),
            ("今日やる", ColumnRole.Today, 1),
            ("完了", ColumnRole.Done, 2));
    }

    [Fact]
    public async Task Migrate_PutsTheTodayColumnFirst_WhenThereIsNoBacklog()
    {
        await SeedBeforeTodayAsync(("進行中", ColumnRole.Active), ("完了", ColumnRole.Done));

        (await ColumnsAfterMigrateAsync()).Should().Equal(
            ("今日中", ColumnRole.Today, 0),
            ("進行中", ColumnRole.Active, 1),
            ("完了", ColumnRole.Done, 2));
    }

    [Fact]
    public async Task Migrate_PutsTheTodayColumnAfterTheFirstOfTwoBacklogs()
    {
        await SeedBeforeTodayAsync(
            ("受信箱", ColumnRole.Backlog), ("いつか", ColumnRole.Backlog), ("完了", ColumnRole.Done));

        (await ColumnsAfterMigrateAsync()).Should().Equal(
            ("受信箱", ColumnRole.Backlog, 0),
            ("今日中", ColumnRole.Today, 1),
            ("いつか", ColumnRole.Backlog, 2),
            ("完了", ColumnRole.Done, 3));
    }

    [Fact]
    public async Task Migrate_OnAnEmptyFile_AddsNoColumn()
    {
        await using var ctx = _db.CreateContext();
        await ctx.Database.MigrateAsync();

        (await ctx.Columns.CountAsync()).Should().Be(0, "盤面が無ければ何もしない。既定のボードは DatabaseInitializer が入れる");
    }

    public void Dispose() => _db.Dispose();
}
