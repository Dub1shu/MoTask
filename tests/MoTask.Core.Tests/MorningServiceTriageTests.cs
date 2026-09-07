using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 仕分けの 4 アクション(仕様 §10)。BoardService は実物を同じ OperationGate で組むので、
/// ゲートの中から IBoardService を呼ぶ実装にするとこのクラスのテストが返ってこなくなる。
/// </summary>
public class MorningServiceTriageTests
{
    /// <summary>デッドロックを「失敗」として見えるようにする上限。</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly Column _backlog;
    private readonly TaskItem _target;
    private readonly MorningRun _run;

    public MorningServiceTriageTests()
    {
        var gate = new OperationGate();
        _backlog = _store.SeedColumn("やること", ColumnRole.Backlog);
        var active = _store.SeedColumn("今日中", ColumnRole.Active);
        _target = _store.SeedTask(active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _run = _store.SeedRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
    }

    private TriageCandidate Candidate(TriageAction suggested = TriageAction.Register)
    {
        var candidate = _store.SeedCandidate(_run, "outlook:001", suggested: suggested);
        candidate.SuggestedDueDate = new DateOnly(2026, 9, 8);
        candidate.SuggestedProject = "顧客A";
        return candidate;
    }

    private static async Task<T> WithinLimitAsync<T>(Task<T> work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(Limit));
        finished.Should().BeSameAs(work, "ゲートの中から IBoardService を呼ぶとデッドロックする");
        return await work;
    }

    // ---- 登録 ----

    [Fact]
    public async Task Register_CreatesTheTaskWithTheEditedValues()
    {
        var candidate = Candidate();

        var created = await WithinLimitAsync(_service.RegisterAsync(new CandidateDecision(
            candidate.Id, "  請求先情報を更新する  ", new DateOnly(2026, 9, 9), "顧客A", _backlog.Id)));

        created.IsSuccess.Should().BeTrue(created.Error);
        var task = created.Value!;
        task.Title.Should().Be("請求先情報を更新する", "人が編集した値を使う");
        task.ColumnId.Should().Be(_backlog.Id);
        task.DueDate.Should().Be(new DateOnly(2026, 9, 9));
        _store.Projects.Should().ContainSingle().Which.Name.Should().Be("顧客A");
        task.ProjectId.Should().Be(_store.Projects[0].Id);
    }

    [Fact]
    public async Task Register_PutsTheEvidenceIntoTheDescription()
    {
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)))).Value!;

        task.Description.Should().Contain("Outlook");
        task.Description.Should().Contain("山本さん");
        task.Description.Should().Contain("「9月8日までに」");
        task.Description.Should().Contain("https://outlook.office.com/x");
    }

    [Fact]
    public async Task Register_MarksTheCandidateAndLeavesOneHistoryEntry()
    {
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)))).Value!;

        candidate.Status.Should().Be(TriageStatus.Registered);
        candidate.ResultTaskId.Should().Be(task.Id);
        candidate.DecidedAt.Should().Be(_clock.UtcNow);
        _store.History.Where(h => h.Kind == HistoryKind.CandidateRegistered).Should().ContainSingle()
            .Which.TaskId.Should().Be(task.Id);
    }

    [Fact]
    public async Task Register_ReusesAnExistingProjectByName()
    {
        var existing = _store.SeedProject("顧客A");
        var candidate = Candidate();

        var task = (await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "  顧客A  ", _backlog.Id)))).Value!;

        task.ProjectId.Should().Be(existing.Id);
        _store.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task Register_RefusesABlankTitle()
    {
        var candidate = Candidate();

        var created = await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "   ", null, "", _backlog.Id)));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(Messages.TitleRequired);
        candidate.Status.Should().Be(TriageStatus.Pending);
    }

    [Fact]
    public async Task Register_RefusesACandidateThatIsAlreadyDecided()
    {
        var candidate = Candidate();
        candidate.Status = TriageStatus.Rejected;

        var created = await WithinLimitAsync(_service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)));

        created.IsSuccess.Should().BeFalse();
        created.Error.Should().Be(Messages.CandidateAlreadyDecided);
    }

    /// <summary>
    /// タスクは作れたが仕上げの書き込み(UpdateTaskAsync)が失敗したときの後始末。半端なタスクを
    /// 残すと、候補が Pending のままなので人がもう一度登録し直したときに二重にタスクができる。
    /// UpdateTaskAsync だけを狙って落とす必要があるので、InMemoryStore の FailNextSave は使えない
    /// (先に走る CreateTaskAsync の保存を落としてしまい、狙った場面を再現できない)。代わりに
    /// UpdateTaskAsync だけを失敗させる IBoardService のラッパーを使う。
    /// </summary>
    [Fact]
    public async Task Register_DeletesTheOrphanedTask_WhenUpdateFailsAfterCreate()
    {
        var gate = new OperationGate();
        var realBoardService = new BoardService(_store, _store, _store, _clock, gate);
        var boardService = new UpdateFailingBoardService(realBoardService);
        var service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        var candidate = Candidate();

        var created = await WithinLimitAsync(service.RegisterAsync(
            new CandidateDecision(candidate.Id, "請求先情報を更新する", null, "", _backlog.Id)));

        created.IsSuccess.Should().BeFalse();
        candidate.Status.Should().Be(TriageStatus.Pending, "失敗したら候補はやり直せる状態のまま");
        candidate.ResultTaskId.Should().BeNull();
        _store.AllTasks.Where(t => !t.IsDeleted && t.Title == "請求先情報を更新する").Should()
            .BeEmpty("作りかけのタスクを列に残さない(残すと再登録で二重にできてしまう)");
    }

    /// <summary>UpdateTaskAsync だけを失敗させ、それ以外はそのまま本物へ委譲する。</summary>
    private sealed class UpdateFailingBoardService : IBoardService
    {
        private readonly IBoardService _inner;
        public UpdateFailingBoardService(IBoardService inner) => _inner = inner;

        public Task<Result> UpdateTaskAsync(TaskUpdate update, CancellationToken ct = default)
            => Task.FromResult(Result.Fail("テスト用の更新失敗"));

        public Task<Result<Board>> GetBoardAsync(CancellationToken ct = default) => _inner.GetBoardAsync(ct);
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int taskId, CancellationToken ct = default) => _inner.GetHistoryAsync(taskId, ct);
        public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default) => _inner.GetProjectsAsync(ct);
        public Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct = default) => _inner.GetLabelsAsync(ct);
        public Task<Result<TaskItem>> CreateTaskAsync(int columnId, string title, CancellationToken ct = default) => _inner.CreateTaskAsync(columnId, title, ct);
        public Task<Result> MoveTaskAsync(int taskId, int toColumnId, int position, CancellationToken ct = default) => _inner.MoveTaskAsync(taskId, toColumnId, position, ct);
        public Task<Result> DeleteTaskAsync(int taskId, CancellationToken ct = default) => _inner.DeleteTaskAsync(taskId, ct);
        public Task<Result> RestoreTaskAsync(int taskId, CancellationToken ct = default) => _inner.RestoreTaskAsync(taskId, ct);
        public Task<Result> SetTaskLabelsAsync(int taskId, IReadOnlyCollection<int> labelIds, CancellationToken ct = default) => _inner.SetTaskLabelsAsync(taskId, labelIds, ct);
        public Task<Result<Column>> AddColumnAsync(string name, ColumnRole role = ColumnRole.Active, CancellationToken ct = default) => _inner.AddColumnAsync(name, role, ct);
        public Task<Result> RenameColumnAsync(int columnId, string name, CancellationToken ct = default) => _inner.RenameColumnAsync(columnId, name, ct);
        public Task<Result> SetColumnRoleAsync(int columnId, ColumnRole role, CancellationToken ct = default) => _inner.SetColumnRoleAsync(columnId, role, ct);
        public Task<Result> ReorderColumnsAsync(IReadOnlyList<int> orderedColumnIds, CancellationToken ct = default) => _inner.ReorderColumnsAsync(orderedColumnIds, ct);
        public Task<Result> SetWipLimitAsync(int columnId, int? wipLimit, CancellationToken ct = default) => _inner.SetWipLimitAsync(columnId, wipLimit, ct);
        public Task<Result> DeleteColumnAsync(int columnId, CancellationToken ct = default) => _inner.DeleteColumnAsync(columnId, ct);
        public Task<Result<Project>> CreateProjectAsync(string name, CancellationToken ct = default) => _inner.CreateProjectAsync(name, ct);
        public Task<Result> ArchiveProjectAsync(int projectId, CancellationToken ct = default) => _inner.ArchiveProjectAsync(projectId, ct);
        public Task<Result> UnarchiveProjectAsync(int projectId, CancellationToken ct = default) => _inner.UnarchiveProjectAsync(projectId, ct);
        public Task<Result> SetProjectWorkingDirectoryAsync(int projectId, string? path, CancellationToken ct = default) => _inner.SetProjectWorkingDirectoryAsync(projectId, path, ct);
        public Task<Result<Label>> CreateLabelAsync(string name, string color, CancellationToken ct = default) => _inner.CreateLabelAsync(name, color, ct);
        public Task<Result> ArchiveLabelAsync(int labelId, CancellationToken ct = default) => _inner.ArchiveLabelAsync(labelId, ct);
        public Task<Result> UnarchiveLabelAsync(int labelId, CancellationToken ct = default) => _inner.UnarchiveLabelAsync(labelId, ct);
    }

    // ---- 統合 ----

    [Fact]
    public async Task Merge_AppendsTheEvidenceToTheTargetDescription()
    {
        _target.Description = "前からある説明";
        var candidate = Candidate(TriageAction.Merge);

        var merged = await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        merged.IsSuccess.Should().BeTrue(merged.Error);
        _target.Description.Should().StartWith("前からある説明");
        _target.Description.Should().Contain("「9月8日までに」");
    }

    [Fact]
    public async Task Merge_FillsTheDueDateOnlyWhenTheTaskHasNone()
    {
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        _target.DueDate.Should().Be(new DateOnly(2026, 9, 8), "候補側にだけ期限があるとき(仕様 §10)");
    }

    [Fact]
    public async Task Merge_KeepsAnExistingDueDate()
    {
        _target.DueDate = new DateOnly(2026, 9, 30);
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        _target.DueDate.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public async Task Merge_MarksTheCandidateAndLeavesOneHistoryEntry()
    {
        var candidate = Candidate(TriageAction.Merge);

        await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        candidate.Status.Should().Be(TriageStatus.Merged);
        candidate.ResultTaskId.Should().Be(_target.Id);
        _store.History.Where(h => h.Kind == HistoryKind.CandidateMerged).Should().ContainSingle()
            .Which.TaskId.Should().Be(_target.Id);
    }

    [Fact]
    public async Task Merge_RefusesADeletedTarget()
    {
        _target.DeletedAt = _clock.UtcNow;
        var candidate = Candidate(TriageAction.Merge);

        var merged = await WithinLimitAsync(_service.MergeAsync(candidate.Id, _target.Id));

        merged.IsSuccess.Should().BeFalse();
        merged.Error.Should().Be(Messages.TaskNotFound);
        candidate.Status.Should().Be(TriageStatus.Pending);
    }

    // ---- あとで / 却下 ----

    [Fact]
    public async Task Postpone_OnlyChangesTheStatus()
    {
        var candidate = Candidate();

        var postponed = await WithinLimitAsync(_service.PostponeAsync(candidate.Id));

        postponed.IsSuccess.Should().BeTrue(postponed.Error);
        candidate.Status.Should().Be(TriageStatus.Later);
        candidate.DecidedAt.Should().Be(_clock.UtcNow);
        candidate.ResultTaskId.Should().BeNull();
        _store.AllTasks.Should().ContainSingle("タスクは作らない");
        _store.History.Should().NotContain(h => h.Kind == HistoryKind.CandidateRegistered);
    }

    [Fact]
    public async Task Reject_OnlyChangesTheStatus()
    {
        var candidate = Candidate();

        var rejected = await WithinLimitAsync(_service.RejectAsync(candidate.Id));

        rejected.IsSuccess.Should().BeTrue(rejected.Error);
        candidate.Status.Should().Be(TriageStatus.Rejected);
        _store.AllTasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Reject_RefusesAnUnknownCandidate()
    {
        var rejected = await WithinLimitAsync(_service.RejectAsync(9999));

        rejected.IsSuccess.Should().BeFalse();
        rejected.Error.Should().Be(Messages.CandidateNotFound);
    }

    // ---- キュー ----

    [Fact]
    public async Task Queue_LosesACandidateAsSoonAsItIsDecided()
    {
        var candidate = Candidate();
        (await _service.GetQueueAsync(_run.Id)).Should().ContainSingle();

        await WithinLimitAsync(_service.PostponeAsync(candidate.Id));

        (await _service.GetQueueAsync(_run.Id)).Should()
            .BeEmpty("『あとで』にした候補は今日のキューから消え、翌朝の実行で戻ってくる(仕様 §9)");
    }

    [Fact]
    public async Task Queue_KeepsLatersFromEarlierRuns()
    {
        var yesterday = _store.SeedRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        _store.SeedCandidate(yesterday, "outlook:old-later", TriageStatus.Later);
        _store.SeedCandidate(yesterday, "outlook:old-rejected", TriageStatus.Rejected);
        Candidate();

        var queue = await _service.GetQueueAsync(_run.Id);

        queue.Select(c => c.ExternalId).Should().Equal("outlook:old-later", "outlook:001");
    }
}
