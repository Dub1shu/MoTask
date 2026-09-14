using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class AiRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    private async Task<int> SeedTaskAsync()
    {
        await using var ctx = _db.CreateContext();
        await new DatabaseInitializer(ctx).InitializeAsync();
        var backlog = await ctx.Columns.SingleAsync(c => c.Role == ColumnRole.Backlog);
        var now = DateTime.UtcNow;
        var task = new TaskItem { Title = "t", ColumnId = backlog.Id, CreatedAt = now, UpdatedAt = now };
        ctx.Tasks.Add(task);
        await ctx.SaveChangesAsync();
        return task.Id;
    }

    [Fact]
    public async Task AiJob_RoundTrips_AndOrdersAsSpecified()
    {
        var taskId = await SeedTaskAsync();
        var session = Guid.NewGuid();
        await using (var ctx = _db.CreateContext())
        {
            var repo = new AiJobRepository(ctx);
            var older = new AiJob { TaskId = taskId, Kind = AiJobKind.Research, Status = AiJobStatus.Succeeded, SessionId = Guid.NewGuid(), JobFolder = @"C:\w\jobs\0001-t", NumTurns = 4 };
            var newer = new AiJob { TaskId = taskId, Kind = AiJobKind.Execute, Status = AiJobStatus.WaitingForInput, SessionId = session, Instruction = "やる", WorkingDirectory = @"C:\w" };
            repo.Add(older);
            repo.Add(newer);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new AiJobRepository(ctx);
            var forTask = await repo.GetForTaskAsync(taskId);
            forTask.Select(j => j.Kind).Should().Equal(AiJobKind.Execute, AiJobKind.Research);
            forTask[0].SessionId.Should().Be(session);
            forTask[1].JobFolder.Should().Be(@"C:\w\jobs\0001-t");

            var unfinished = await repo.GetByStatusAsync(new[] { AiJobStatus.WaitingForInput, AiJobStatus.Running });
            unfinished.Should().ContainSingle().Which.Status.Should().Be(AiJobStatus.WaitingForInput);
        }
    }

    /// <summary>
    /// BoardService と AiJobService が同じ singleton DbContext を、同じ OperationGate 越しに使えること。
    /// ゲートを共有していないと「A second operation was started on this context」で落ちる。
    /// </summary>
    [Fact]
    public async Task AiJobService_OverSqlite_StartsAndCompletes_MovingTheTaskToReview()
    {
        var events = new CapturingEventSource();
        await using var services = new ServiceCollection()
            .AddMoTaskData(_db.Path)
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBoardService, BoardService>()
            .AddSingleton<ISessionLauncher, NoopLauncher>()
            .AddSingleton<IJobFolder, NoopJobFolder>()
            .AddSingleton<IJobEventSource>(events)
            .AddSingleton<IAiJobService, AiJobService>()
            .BuildServiceProvider();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        services.GetRequiredService<IAiSettingsStore>().Save(AiSettings.Default() with
        {
            DefaultWorkingDirectory = Path.Combine(Path.GetDirectoryName(_db.Path)!, "work-" + Guid.NewGuid().ToString("N")),
        });
        var board = services.GetRequiredService<IBoardService>();
        var jobs = services.GetRequiredService<IAiJobService>();
        var backlog = (await board.GetBoardAsync()).Value!.Columns.Single(c => c.Role == ColumnRole.Backlog);
        var review = (await board.GetBoardAsync()).Value!.Columns.Single(c => c.Role == ColumnRole.Review);
        var task = (await board.CreateTaskAsync(backlog.Id, "永続化")).Value!;
        AiJobSnapshot? final = null;
        jobs.JobChanged += (_, e) => { if (e.Job.Status.IsTerminal()) final = e.Job; };

        var started = await jobs.StartJobAsync(task.Id, AiJobKind.Execute, "やる");
        started.IsSuccess.Should().BeTrue(started.Error);
        // フックが 1 往復ぶんの行を書いたことにする
        await events.EmitAsync(started.Value!.Id, """{"hook_event_name":"SessionStart","source":"startup"}""");
        await events.EmitAsync(started.Value!.Id, """{"hook_event_name":"Stop","last_assistant_message":"done"}""");
        await events.EmitAsync(started.Value!.Id, """{"hook_event_name":"SessionEnd","reason":"exit"}""");

        final.Should().NotBeNull();
        final!.Status.Should().Be(AiJobStatus.Succeeded);
        await using var ctx = _db.CreateContext();
        (await ctx.Tasks.SingleAsync()).ColumnId.Should().Be(review.Id);
        var job = await ctx.Set<AiJob>().SingleAsync();
        job.Status.Should().Be(AiJobStatus.Succeeded);
        job.NumTurns.Should().Be(1);
        job.JobFolder.Should().Be(NoopJobFolder.Root);
        job.ProcessedLines.Should().Be(3);
        (await ctx.History.OrderBy(h => h.Id).Select(h => h.Kind).ToListAsync())
            .Should().Equal(HistoryKind.Created, HistoryKind.AiJobStarted, HistoryKind.AiJobFinished, HistoryKind.Moved);
    }

    /// <summary>端末は開かない。組み立てだけ返す。</summary>
    private sealed class NoopLauncher : ISessionLauncher
    {
        public Result CheckAvailable() => Result.Ok();

        public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
            => Result.Ok(new TerminalCommand("wt.exe", "cmd /k claude", request.WorkingDirectory));

        public Result Launch(TerminalCommand command) => Result.Ok();

        public Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command)
            => Result.Ok(new OwnedSession(0, DateTime.UtcNow));

        public void CloseOwned(int ownerId) { }

        public bool TryReattach(int ownerId, int processId, DateTime startedAt) => false;

#pragma warning disable CS0067 // このテストは端末を持たないので所有プロセスが終了することもない
        public event EventHandler<int>? OwnedSessionExited;
#pragma warning restore CS0067
    }

    /// <summary>ファイルは作らない。決め打ちのパスを返すだけ。</summary>
    private sealed class NoopJobFolder : IJobFolder
    {
        public const string Root = @"C:\work\jobs\0001-permanence";

        public Result<string> Create(JobFolderRequest request) => Result.Ok(Root);
        public void WriteJobJson(string root, JobDescriptor descriptor) { }
        public IReadOnlyList<string> ListArtifacts(string root) => Array.Empty<string>();
        public IReadOnlyList<string> ReadTail(string root, int lines) => Array.Empty<string>();
        public string ResolveRoot(JobFolderRequest request) => Root;
        public Result WriteText(string root, string relativePath, string content) => Result.Ok();
        public string? ReadText(string root, string relativePath) => null;
    }

    /// <summary>events.jsonl の代わりにテストが行を流し込む。</summary>
    private sealed class CapturingEventSource : IJobEventSource
    {
        private readonly List<JobEventSubscription> _subscriptions = new();

        public void Follow(JobEventSubscription subscription) => _subscriptions.Add(subscription);

        public void StopFollowing(int jobId) { }

        public Task EmitAsync(int jobId, string line) => _subscriptions.Last(s => s.JobId == jobId).OnLine(line);
    }

    public void Dispose() => _db.Dispose();
}
