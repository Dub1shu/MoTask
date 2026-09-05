using System.Text.Json;
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class AiJobServicePermissionTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly OperationGate _gate = new();
    private readonly FakeAgentRunner _runner = new();
    private readonly FakePermissionPrompt _prompt = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly AiJobService _service;
    private readonly Column _backlog;
    private readonly Project _project;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    private static readonly PermissionRequest GitPush =
        new("Bash", """{"command":"git push origin main","description":"push"}""", "toolu_1");

    public AiJobServicePermissionTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("確認待ち", ColumnRole.Review);
        _store.SeedColumn("完了", ColumnRole.Done);
        _project = _store.SeedProject("顧客A");
        _settings.Settings = AiSettings.Default() with { DefaultWorkingDirectory = _tempDir };
        var boardService = new BoardService(_store, _store, _store, _clock, _gate);
        _service = new AiJobService(_store, _store, _store, _store, _store, _clock, _gate, _runner,
            new PermissionPolicy(), _prompt, _settings, boardService);
    }

    private async Task<AiJob> StartAsync(bool withProject = true)
    {
        var task = _store.SeedTask(_backlog, "デプロイ");
        if (withProject) task.ProjectId = _project.Id;
        var job = (await _service.StartJobAsync(task.Id, AiJobKind.Execute, "デプロイして")).Value!;
        await _runner.WaitForRunAsync(job.Id);
        return job;
    }

    private static AiPermissionRule Rule(string tool, string? pattern, RuleDecision decision, RuleScope scope = RuleScope.Global, int? projectId = null)
        => new() { ToolName = tool, Pattern = pattern, Decision = decision, Scope = scope, ProjectId = projectId, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task RuleAllow_ReturnsAllow_WithoutAskingAHuman_AndRecordsBothEvents()
    {
        _store.Rules.Add(Rule("Bash", "git push", RuleDecision.Allow));
        var job = await StartAsync();
        var statuses = new List<AiJobStatus>();
        _service.JobChanged += (_, e) => statuses.Add(e.Job.Status);

        var decision = await _runner.AskPermissionAsync(job.Id, GitPush);

        decision.IsAllowed.Should().BeTrue();
        _prompt.Asked.Should().BeEmpty();
        job.Status.Should().Be(AiJobStatus.Running);
        statuses.Should().Equal(AiJobStatus.AwaitingApproval, AiJobStatus.Running);

        var events = await _service.GetEventsAsync(job.Id);
        events.Select(e => e.Kind).Should().Equal(AiJobEventKind.PermissionAsked, AiJobEventKind.PermissionDecided);
        events.Should().OnlyContain(e => e.ToolName == "Bash");
        using var asked = JsonDocument.Parse(events[0].Payload);
        asked.RootElement.GetProperty("type").GetString().Should().Be("motask_permission_asked");
        asked.RootElement.GetProperty("input").GetProperty("command").GetString().Should().Be("git push origin main");
        using var decided = JsonDocument.Parse(events[1].Payload);
        decided.RootElement.GetProperty("behavior").GetString().Should().Be("allow");
        decided.RootElement.GetProperty("source").GetString().Should().Be("rule");
    }

    [Fact]
    public async Task RuleDeny_ReturnsDeny_WithRuleMessage()
    {
        _store.Rules.Add(Rule("Bash", "git push", RuleDecision.Deny));
        var job = await StartAsync();

        var decision = await _runner.AskPermissionAsync(job.Id, GitPush);

        decision.IsAllowed.Should().BeFalse();
        decision.Message.Should().Be(Messages.DeniedByRule);
        _prompt.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task AskHuman_WhileWaiting_StatusIsAwaitingApproval_AndContextIsFilled()
    {
        var job = await StartAsync();

        var pending = _runner.AskPermissionAsync(job.Id, GitPush);
        await _prompt.WaitUntilAskedAsync();

        job.Status.Should().Be(AiJobStatus.AwaitingApproval);
        var ctx = _prompt.Asked.Single();
        ctx.Job.Should().BeSameAs(job);
        ctx.TaskTitle.Should().Be("デプロイ");
        ctx.ProjectId.Should().Be(_project.Id);
        ctx.Request.Should().Be(GitPush);
        ctx.RememberPattern.Should().Be("git push");

        _prompt.Answer(new HumanDecision(RuleDecision.Allow, Remember: false, RuleScope.Global));
        (await pending).IsAllowed.Should().BeTrue();
        job.Status.Should().Be(AiJobStatus.Running);
        _store.Rules.Should().BeEmpty("記憶しない");
    }

    [Fact]
    public async Task AskHuman_Deny_ReturnsHumanMessage()
    {
        _prompt.Enqueue(new HumanDecision(RuleDecision.Deny, Remember: false, RuleScope.Global));
        var job = await StartAsync();

        var decision = await _runner.AskPermissionAsync(job.Id, GitPush);

        decision.IsAllowed.Should().BeFalse();
        decision.Message.Should().Be(Messages.DeniedByHuman);
        using var decided = JsonDocument.Parse((await _service.GetEventsAsync(job.Id))[1].Payload);
        decided.RootElement.GetProperty("source").GetString().Should().Be("human");
        decided.RootElement.GetProperty("message").GetString().Should().Be(Messages.DeniedByHuman);
    }

    [Fact]
    public async Task AskHuman_AllowAlways_ProjectScope_CreatesProjectRule_WithRememberPattern()
    {
        _prompt.Enqueue(new HumanDecision(RuleDecision.Allow, Remember: true, RuleScope.Project));
        var job = await StartAsync();
        _clock.UtcNow = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        (await _runner.AskPermissionAsync(job.Id, GitPush)).IsAllowed.Should().BeTrue();

        var rule = _store.Rules.Should().ContainSingle().Subject;
        rule.Scope.Should().Be(RuleScope.Project);
        rule.ProjectId.Should().Be(_project.Id);
        rule.ToolName.Should().Be("Bash");
        rule.Pattern.Should().Be("git push");
        rule.Decision.Should().Be(RuleDecision.Allow);
        rule.CreatedAt.Should().Be(_clock.UtcNow);

        // 次の同種の要求はダイアログ無しで通る
        (await _runner.AskPermissionAsync(job.Id, GitPush with { ToolUseId = "toolu_2" })).IsAllowed.Should().BeTrue();
        _prompt.Asked.Should().ContainSingle();
    }

    [Fact]
    public async Task AskHuman_DenyAlways_GlobalScope_CreatesGlobalRule()
    {
        _prompt.Enqueue(new HumanDecision(RuleDecision.Deny, Remember: true, RuleScope.Global));
        var job = await StartAsync();
        var write = new PermissionRequest("Write", """{"file_path":"C:\\work\\proj\\out.md","content":"x"}""", "toolu_3");

        (await _runner.AskPermissionAsync(job.Id, write)).IsAllowed.Should().BeFalse();

        var rule = _store.Rules.Should().ContainSingle().Subject;
        rule.Scope.Should().Be(RuleScope.Global);
        rule.ProjectId.Should().BeNull();
        rule.ToolName.Should().Be("Write");
        rule.Pattern.Should().Be(@"C:\work\proj");
        rule.Decision.Should().Be(RuleDecision.Deny);
    }

    [Fact]
    public async Task AskHuman_ProjectScopeWithoutProject_FallsBackToGlobal()
    {
        _prompt.Enqueue(new HumanDecision(RuleDecision.Allow, Remember: true, RuleScope.Project));
        var job = await StartAsync(withProject: false);

        await _runner.AskPermissionAsync(job.Id, GitPush);

        var rule = _store.Rules.Should().ContainSingle().Subject;
        rule.Scope.Should().Be(RuleScope.Global);
        rule.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task AskHuman_ToolWithoutPattern_RemembersWholeTool()
    {
        _prompt.Enqueue(new HumanDecision(RuleDecision.Allow, Remember: true, RuleScope.Global));
        var job = await StartAsync();
        var fetch = new PermissionRequest("WebFetch", """{"url":"https://example.com"}""", "toolu_4");

        await _runner.AskPermissionAsync(job.Id, fetch);

        _prompt.Asked.Single().RememberPattern.Should().BeNull();
        _store.Rules.Single().Pattern.Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
