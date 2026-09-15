using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 朝の実行の開始（仕様 §6・§12）。フォルダを先に作り、パスが確定してから DB に保存する。
/// </summary>
public class MorningServiceStartTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new() { Today = new DateOnly(2026, 9, 7) };
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeSessionLauncher _launcher = new();
    private readonly FakeJobFolder _folder = new();
    private readonly FakeJobEventSource _events = new();
    private readonly MorningService _service;
    private readonly Column _active;
    private readonly List<MorningRunChangedEventArgs> _changes = new();

    public MorningServiceStartTests()
    {
        var gate = new OperationGate();
        _store.SeedColumn("やること", ColumnRole.Backlog);
        _active = _store.SeedColumn("今日中", ColumnRole.Active);
        _store.SeedColumn("完了", ColumnRole.Done);
        _store.SeedTask(_active, "Q4企画書の内容を確定する");
        var boardService = new BoardService(_store, _store, _store, _clock, gate);
        _service = new MorningService(_store, _store, _store, _store, _store, _clock, gate,
            _launcher, _folder, _events, _settings, boardService);
        _service.RunChanged += (_, e) => { lock (_changes) _changes.Add(e); };
    }

    [Fact]
    public async Task Start_PutsTheFolderUnderMorning_NamedByRunNumberAndDate()
    {
        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeTrue(started.Error);
        _folder.Created.Should().ContainSingle();
        _folder.Created[0].Category.Should().Be(JobFolderPaths.MorningDirectoryName);
        _folder.Created[0].OutputDirectoryName.Should().BeNull("朝の実行に出力フォルダは要らない");
        _folder.Created[0].TaskTitle.Should().Be("2026-09-07");
        started.Value!.JobFolder.Should().Be(@"C:\work\morning\0001-2026-09-07");
    }

    [Fact]
    public async Task Start_RunsInsideTheJobFolderItself()
    {
        await _service.StartAsync();

        var request = _launcher.Requests.Should().ContainSingle().Subject;
        request.WorkingDirectory.Should().Be(request.JobFolder, "朝の実行はソースツリーに用が無い（仕様 §6）");
        request.Resume.Should().BeFalse();
    }

    [Fact]
    public async Task Start_SavesTheRunOnlyAfterTheFolderIsKnown()
    {
        var run = (await _service.StartAsync()).Value!;

        run.Status.Should().Be(MorningRunStatus.Pending);
        run.JobFolder.Should().NotBeEmpty("JobFolder が空のまま Pending で残る窓を作らない（仕様 §12）");
        run.Instruction.Should().Contain($"runId は {run.Id} です", "契約文は runId を名指しする（仕様 §8）");
        run.SessionId.Should().NotBeEmpty();
        run.StartedAt.Should().Be(_clock.UtcNow);
        _store.Runs.Should().ContainSingle();
    }

    /// <summary>フォルダの instruction.md と DB の Instruction 列は同じ文言（仕様 §8）。</summary>
    [Fact]
    public async Task Start_WritesTheSameInstructionToTheFolderAndTheRow()
    {
        var run = (await _service.StartAsync()).Value!;

        _folder.ReadText(run.JobFolder, JobFolderPaths.InstructionMarkdownName).Should().Be(run.Instruction);
        run.Instruction.Should().Contain("mcp__motask__morning_complete");
    }

    [Fact]
    public async Task Start_WritesRunJsonWithTheLaunchCommand()
    {
        var run = (await _service.StartAsync()).Value!;

        var text = _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName);
        text.Should().NotBeNull("DB が壊れてもフォルダだけで何の実行か分かるようにする（仕様 §6）");
        var root = JsonDocument.Parse(text!).RootElement;
        root.GetProperty("runId").GetInt32().Should().Be(run.Id);
        root.GetProperty("launchCommand").GetString().Should().StartWith("cmd.exe /c ");
    }

    /// <summary>朝の実行は claude が終われば窓も畳む形で頼む（仕様 §5.2）。</summary>
    [Fact]
    public async Task Start_AsksForATerminalThatClosesWhenClaudeExits()
    {
        await _service.StartAsync();

        _launcher.Requests.Should().ContainSingle().Which.CloseOnExit.Should().BeTrue();
    }

    [Fact]
    public async Task Start_DoesNotAskThePromptToPointAtAnOutputFolder()
    {
        await _service.StartAsync();

        _launcher.Requests.Should().ContainSingle().Which.OutputDirectoryName.Should().BeNull();
    }

    /// <summary>完了時に窓を閉じるには Process ハンドルが要る（仕様 §5.3）。</summary>
    [Fact]
    public async Task Start_OwnsTheTerminalItOpened()
    {
        var run = (await _service.StartAsync()).Value!;

        var owned = _launcher.LaunchedOwned.Should().ContainSingle().Subject;
        owned.OwnerId.Should().Be(run.Id, "ownerId は runId（宛先を取り違えない）");
        owned.Command.FileName.Should().Be("cmd.exe");
    }

    /// <summary>再起動した MoTask はここから掛け直す（仕様 §7）。</summary>
    [Fact]
    public async Task Start_WritesTheProcessIdAndStartTimeIntoRunJson()
    {
        // Process.StartTime は 100ns tick の精度を持ち、TryReattach はこの値を完全一致で照合する
        // （仕様 §7）。秒丸めの値だと run.json 経由で精度が落ちても気づけない。
        var processStartedAt = new DateTime(2026, 9, 13, 6, 0, 1, DateTimeKind.Utc).AddTicks(1234567);
        _launcher.Session = new OwnedSession(31337, processStartedAt);

        var run = (await _service.StartAsync()).Value!;

        var text = _folder.ReadText(run.JobFolder, JobFolderPaths.RunJsonName);
        var descriptor = MorningRunDescriptor.TryParse(text);
        descriptor.Should().NotBeNull();
        descriptor!.ProcessId.Should().Be(31337);
        descriptor.ProcessStartedAt.Should().Be(processStartedAt);
    }

    [Fact]
    public async Task Start_FollowsTheEventsFileFromTheTop()
    {
        var run = (await _service.StartAsync()).Value!;

        _events.IsFollowing(run.Id).Should().BeTrue();
        _events.SkipLinesOf(run.Id).Should().Be(0);
        _events.EventsPathOf(run.Id).Should().Be(JobFolderPaths.For(run.JobFolder).EventsJsonl);
    }

    [Fact]
    public async Task Start_RefusesASecondRunWhileOneIsUnfinished()
    {
        await _service.StartAsync();

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeFalse("二重起動の防止（仕様 §12）");
        second.Error.Should().Be(Messages.MorningRunAlreadyRunning);
        _store.Runs.Should().ContainSingle();
        _folder.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_IsAllowedAgainOnceTheLastRunIsFinished()
    {
        var first = (await _service.StartAsync()).Value!;
        first.Status = MorningRunStatus.Ingested;

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.JobFolder.Should().Be(@"C:\work\morning\0002-2026-09-07", "連番は実行の件数で決まる");
    }

    [Fact]
    public async Task Start_StopsBeforeDoingAnything_WhenClaudeIsMissing()
    {
        _launcher.Availability = Result.Fail(Messages.ClaudeNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.ClaudeNotFound);
        _store.Runs.Should().BeEmpty("黙って走らせず、開始時点で止める（仕様 §12）");
        _folder.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_StopsBeforeSaving_WhenTheHooksExeIsMissing()
    {
        _folder.CreateFailure = Result.Fail<string>(Messages.HooksExecutableNotFound);

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        started.Error.Should().Be(Messages.HooksExecutableNotFound);
        _store.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_MarksTheRunFailed_WhenTheTerminalWillNotOpen()
    {
        _launcher.LaunchFailure = Result.Fail("端末を起動できませんでした");

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        var run = _store.Runs.Should().ContainSingle().Subject;
        run.Status.Should().Be(MorningRunStatus.Failed);
        run.ErrorMessage.Should().Be("端末を起動できませんでした");
        run.JobFolder.Should().NotBeEmpty("失敗した実行でもフォルダは開ける");
        _events.IsFollowing(run.Id).Should().BeFalse();
    }

    /// <summary>
    /// 1 回目の保存（Add）は通って Id が採番された後、2 回目（指示文の書き戻し）だけが
    /// 落ちた場合。行を Pending のまま残すと、次の StartAsync が二重起動防止に引っかかって
    /// 朝の実行が永久に始められなくなる（仕様 §12）。ベストエフォートで Failed に倒す。
    /// </summary>
    [Fact]
    public async Task Start_MarksTheRunFailed_WhenOnlyTheSecondSaveFails()
    {
        _store.FailSaveAtCount = 2;

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeFalse();
        var run = _store.Runs.Should().ContainSingle().Subject;
        run.Status.Should().Be(MorningRunStatus.Failed, "Pending のまま残すと次の実行を永久に塞ぐ");
        run.ErrorMessage.Should().Contain(Messages.SaveFailed);

        var second = await _service.StartAsync();

        second.IsSuccess.Should().BeTrue(second.Error, "終端に倒れているので次の実行は塞がれない");
    }

    /// <summary>
    /// 端末はもう走っているので、run.json が書けなくても実行は続ける。ただし再起動後に
    /// 掛け直せなくなる（仕様 §7）ので、理由を警告として人に見せる。
    /// </summary>
    [Fact]
    public async Task Start_ContinuesWithAWarning_WhenRunJsonCannotBeWritten()
    {
        _folder.WriteFailure = Result.Fail("run.json を書けませんでした");
        _folder.FailWritesTo = JobFolderPaths.RunJsonName;

        var started = await _service.StartAsync();

        started.IsSuccess.Should().BeTrue("端末はもう走っている。JSON 1 本の書き損じで朝の仕事を潰さない");
        var run = started.Value!;
        _events.IsFollowing(run.Id).Should().BeTrue();
        _changes.Should().ContainSingle();
        _changes[0].Warning.Should().Be("run.json を書けませんでした");
    }

    [Fact]
    public async Task Start_UsesTheConfiguredInstructionTemplate()
    {
        _settings.Settings = AiSettings.Default() with { MorningInstruction = "私の方針" };

        var run = (await _service.StartAsync()).Value!;

        run.Instruction.Should().StartWith("私の方針");
        _folder.ReadText(run.JobFolder, JobFolderPaths.InstructionMarkdownName).Should().Be(run.Instruction);
    }

    [Fact]
    public async Task Start_RaisesRunChangedOnce()
    {
        var run = (await _service.StartAsync()).Value!;

        _changes.Should().ContainSingle();
        _changes[0].Run.RunId.Should().Be(run.Id);
        _changes[0].Run.Status.Should().Be(MorningRunStatus.Pending);
        _changes[0].CandidatesChanged.Should().BeFalse();
    }

    [Fact]
    public async Task Start_AsksForTheMcpConfigSoTheToolsAreThereWithoutManualRegistration()
    {
        var run = (await _service.StartAsync()).Value!;

        _folder.Created[0].WithMcpConfig.Should().BeTrue();
        _launcher.Requests.Should().ContainSingle().Which.McpConfigPath
            .Should().Be(JobFolderPaths.For(run.JobFolder).McpJson);
    }

    [Fact]
    public async Task GetCurrentRun_IsTheLatestOne()
    {
        (await _service.GetCurrentRunAsync()).Should().BeNull("まだ一度も走らせていない");

        var run = (await _service.StartAsync()).Value!;

        (await _service.GetCurrentRunAsync())!.Id.Should().Be(run.Id);
    }
}
