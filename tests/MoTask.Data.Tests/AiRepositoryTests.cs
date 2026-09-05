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
    public async Task AiJob_WithEvents_RoundTrips_AndOrdersAsSpecified()
    {
        var taskId = await SeedTaskAsync();
        var session = Guid.NewGuid();
        await using (var ctx = _db.CreateContext())
        {
            var repo = new AiJobRepository(ctx);
            var older = new AiJob { TaskId = taskId, Kind = AiJobKind.Research, Status = AiJobStatus.Succeeded, SessionId = Guid.NewGuid(), TotalCostUsd = 0.1234m, NumTurns = 4 };
            var newer = new AiJob { TaskId = taskId, Kind = AiJobKind.Execute, Status = AiJobStatus.Suspended, SessionId = session, Instruction = "やる", WorkingDirectory = @"C:\w" };
            repo.Add(older);
            repo.Add(newer);
            await ctx.SaveChangesAsync();
            repo.AddEvent(new AiJobEvent { JobId = newer.Id, Seq = 2, At = DateTime.UtcNow, Kind = AiJobEventKind.ToolUse, ToolName = "Bash", Payload = "{\"b\":2}" });
            repo.AddEvent(new AiJobEvent { JobId = newer.Id, Seq = 1, At = DateTime.UtcNow, Kind = AiJobEventKind.AssistantText, Payload = "{\"a\":1}" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new AiJobRepository(ctx);
            var forTask = await repo.GetForTaskAsync(taskId);
            forTask.Select(j => j.Kind).Should().Equal(AiJobKind.Execute, AiJobKind.Research);
            forTask[0].SessionId.Should().Be(session);
            forTask[1].TotalCostUsd.Should().Be(0.1234m);

            var unfinished = await repo.GetByStatusAsync(new[] { AiJobStatus.Suspended, AiJobStatus.Running });
            unfinished.Should().ContainSingle().Which.Status.Should().Be(AiJobStatus.Suspended);

            var events = await repo.GetEventsAsync(forTask[0].Id);
            events.Select(e => e.Seq).Should().Equal(1, 2);
            events[1].ToolName.Should().Be("Bash");
            events[1].Kind.Should().Be(AiJobEventKind.ToolUse);
        }
    }

    [Fact]
    public async Task PermissionRules_RoundTrip_InCreationOrder()
    {
        await using (var ctx = _db.CreateContext())
        {
            await ctx.Database.MigrateAsync();
            var project = new Project { Name = "p" };
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();
            var repo = new PermissionRuleRepository(ctx);
            repo.Add(new AiPermissionRule { Scope = RuleScope.Project, ProjectId = project.Id, ToolName = "Bash", Pattern = "git push", Decision = RuleDecision.Allow, CreatedAt = new DateTime(2026, 9, 5, 1, 0, 0, DateTimeKind.Utc) });
            repo.Add(new AiPermissionRule { Scope = RuleScope.Global, ToolName = "WebFetch", Decision = RuleDecision.Deny, CreatedAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc) });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new PermissionRuleRepository(ctx);
            var all = await repo.GetAllAsync();
            all.Select(r => r.ToolName).Should().Equal("WebFetch", "Bash");
            all[1].Scope.Should().Be(RuleScope.Project);
            all[1].ProjectId.Should().NotBeNull();
            all[1].Pattern.Should().Be("git push");
            all[0].Pattern.Should().BeNull();

            repo.Remove(all[0]);
            await new EfUnitOfWork(ctx).SaveChangesAsync();
            (await repo.GetAsync(all[0].Id)).Should().BeNull();
        }
    }

    /// <summary>
    /// BoardService と AiJobService が同じ singleton DbContext を、同じ OperationGate 越しに使えること。
    /// ゲートを共有していないと「A second operation was started on this context」で落ちる。
    /// </summary>
    [Fact]
    public async Task AiJobService_OverSqlite_StartsAndCompletes_MovingTheTaskToReview()
    {
        var runner = new ImmediateRunner();
        await using var services = new ServiceCollection()
            .AddMoTaskData(_db.Path)
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBoardService, BoardService>()
            .AddSingleton<IAgentRunner>(runner)
            .AddSingleton<IPermissionPolicy, PermissionPolicy>()
            .AddSingleton<IPermissionPrompt, DenyPrompt>()
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
        var done = new TaskCompletionSource<AiJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.JobChanged += (_, e) => { if (e.Job.Status.IsTerminal()) done.TrySetResult(e.Job); };

        var started = await jobs.StartJobAsync(task.Id, AiJobKind.Execute, "やる");
        started.IsSuccess.Should().BeTrue(started.Error);
        var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        final.Status.Should().Be(AiJobStatus.Succeeded);
        await using var ctx = _db.CreateContext();
        (await ctx.Tasks.SingleAsync()).ColumnId.Should().Be(review.Id);
        var job = await ctx.Set<AiJob>().SingleAsync();
        job.Status.Should().Be(AiJobStatus.Succeeded);
        job.NumTurns.Should().Be(1);
        (await ctx.Set<AiJobEvent>().CountAsync()).Should().Be(1);
        (await ctx.History.OrderBy(h => h.Id).Select(h => h.Kind).ToListAsync())
            .Should().Equal(HistoryKind.Created, HistoryKind.AiJobStarted, HistoryKind.AiJobFinished, HistoryKind.Moved);
    }

    /// <summary>イベントを 1 つ流して即成功するランナー。</summary>
    private sealed class ImmediateRunner : IAgentRunner
    {
        public Result CheckAvailable() => Result.Ok();

        public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken ct)
        {
            await request.OnEvent(new AgentEvent(AiJobEventKind.AssistantText, null, """{"type":"assistant","message":{"content":[{"type":"text","text":"done"}]}}"""));
            return new AgentRunOutcome(0, new AgentResultInfo(false, 1, 0.01m, "done"), null);
        }
    }

    private sealed class DenyPrompt : IPermissionPrompt
    {
        public Task<HumanDecision> AskAsync(PermissionPromptContext context, CancellationToken ct)
            => Task.FromResult(new HumanDecision(RuleDecision.Deny, false, RuleScope.Global));
    }

    public void Dispose() => _db.Dispose();
}
