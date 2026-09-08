using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 一括操作（仕様 §5）。4 アクションを順に呼ぶだけで、1 件の失敗で止まらない。
/// MorningServiceTriageTests と同じく、ゲートの中から IBoardService を呼ぶ実装にすると返ってこない。
/// </summary>
public class MorningServiceBulkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly MorningService _service;
    private readonly Column _backlog;
    private readonly TaskItem _target;
    private readonly MorningRun _run;

    public MorningServiceBulkTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _target = _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            new FakeSessionLauncher(), new FakeJobFolder(), new FakeJobEventSource(), new InMemorySettingsStore(), boardService);
        _run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
    }

    private TriageCandidate Candidate(string externalId, TriageAction suggested, int? mergeTarget = null)
    {
        var candidate = _store.SeedCandidate(_run, externalId, suggested: suggested);
        candidate.Title = $"候補 {externalId}";
        candidate.SuggestedMergeTaskId = mergeTarget;
        return candidate;
    }

    private static async Task<T> WithinLimitAsync<T>(Task<T> work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(Limit));
        finished.Should().BeSameAs(work, "ゲートの中から IBoardService を呼ぶとデッドロックする");
        return await work;
    }

    [Fact]
    public async Task ApplySuggestions_DispatchesEachCandidateByItsSuggestion()
    {
        var register = Candidate("a", TriageAction.Register);
        var merge = Candidate("b", TriageAction.Merge, _target.Id);
        var later = Candidate("c", TriageAction.Later);
        var reject = Candidate("d", TriageAction.Reject);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue(outcome.Error);
        outcome.Value!.Applied.Should().Be(4);
        outcome.Value.Skipped.Should().BeEmpty();
        register.Status.Should().Be(TriageStatus.Registered);
        _store.AllTasks.Should().Contain(t => t.Id == register.ResultTaskId && t.ColumnId == _backlog.Id && t.Title == "候補 a",
            "登録は候補のタイトルのまま、指定した列へ入る");
        merge.Status.Should().Be(TriageStatus.Merged);
        merge.ResultTaskId.Should().Be(_target.Id);
        later.Status.Should().Be(TriageStatus.Later);
        reject.Status.Should().Be(TriageStatus.Rejected);
    }

    [Fact]
    public async Task ApplySuggestions_SkipsAFailure_AndKeepsGoing()
    {
        var broken = Candidate("a", TriageAction.Merge, mergeTarget: 999);
        var fine = Candidate("b", TriageAction.Reject);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue("全件見送りでも一括操作としては成功");
        outcome.Value!.Applied.Should().Be(1);
        outcome.Value.Skipped.Should().ContainSingle()
            .Which.Should().Be(string.Format(Messages.BulkSkippedFormat, "候補 a", Messages.TaskNotFound));
        broken.Status.Should().Be(TriageStatus.Pending, "見送った候補はキューに残る");
        fine.Status.Should().Be(TriageStatus.Rejected, "1 件目の失敗で 2 件目を止めない");
    }

    [Fact]
    public async Task ApplySuggestions_SkipsAMergeWithoutASuggestedTarget()
    {
        Candidate("a", TriageAction.Merge, mergeTarget: null);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.Value!.Applied.Should().Be(0);
        outcome.Value.Skipped.Should().ContainSingle()
            .Which.Should().Be(string.Format(Messages.BulkSkippedFormat, "候補 a", Messages.MergeTargetMissing));
    }

    [Fact]
    public async Task ApplySuggestions_CollectsTheWarningsOfSuccessfulActions()
    {
        var tight = _store.SeedColumn("狭い列", ColumnRole.Active, wipLimit: 1);
        _store.SeedTask(tight, "既にある");
        Candidate("a", TriageAction.Register);

        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, tight.Id));

        outcome.Value!.Applied.Should().Be(1);
        outcome.Warnings.Should().NotBeEmpty("WIP 超過の警告は成功の警告として集約する");
    }

    [Fact]
    public async Task ApplySuggestions_WithAnEmptyQueue_DoesNothing()
    {
        var outcome = await WithinLimitAsync(_service.ApplySuggestionsAsync(_run.Id, _backlog.Id));

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.Applied.Should().Be(0);
        outcome.Value.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task PostponeAll_MarksEveryQueuedCandidateLater()
    {
        var a = Candidate("a", TriageAction.Register);
        var b = Candidate("b", TriageAction.Reject);
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        var carried = _store.SeedCandidate(yesterday, "old", TriageStatus.Later);

        var outcome = await WithinLimitAsync(_service.PostponeAllAsync(_run.Id));

        outcome.Value!.Applied.Should().Be(3, "過去の Later もキューに乗っているので対象");
        a.Status.Should().Be(TriageStatus.Later);
        b.Status.Should().Be(TriageStatus.Later);
        carried.Status.Should().Be(TriageStatus.Later);
        _store.AllTasks.Should().ContainSingle("タスクは作らない");
    }
}
