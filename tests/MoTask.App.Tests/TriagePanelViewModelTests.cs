using FluentAssertions;
using MoTask.App;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>左パネル・状態 1（仕様 §6）。編集フォームと統合先の選択。</summary>
public class TriagePanelViewModelTests
{
    private readonly FakeMorningService _service = new();
    private readonly List<Result> _decisions = new();
    private readonly List<string> _opened = new();
    private readonly TriagePanelViewModel _panel;

    public TriagePanelViewModelTests()
    {
        _service.RegisterResult = Result.Ok(new TaskItem { Id = 99 });
        // FakeMorningService の 4 アクションは実サービスと同じく Candidates から該当候補を引いて
        // Status を書き換えるので、Candidate() が指す Id=1 を偽サービス側にも 1 件置く。
        _service.Candidates.Add(new TriageCandidate { Id = 1, MorningRunId = 1 });
        _panel = new TriagePanelViewModel(_service, r => { _decisions.Add(r); return Task.CompletedTask; }, _opened.Add);
        _panel.SetChoices(
            new[] { new ColumnChoice(1, "未着手"), new ColumnChoice(2, "進行中") },
            new[] { new TaskChoice(10, "請求先情報を更新する", "未着手"), new TaskChoice(12, "週次レポートを作成する", "進行中") });
    }

    private static CandidateItemViewModel Candidate(int? mergeTarget = null) => new(new TriageCandidate
    {
        Id = 1, MorningRunId = 1, ExternalId = "outlook:001", Source = "Outlook", Title = "請求先情報を更新する",
        Evidence = "「9月8日までに」", Link = "https://outlook.office.com/x",
        SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
        SuggestedAction = mergeTarget is null ? TriageAction.Register : TriageAction.Merge, SuggestedMergeTaskId = mergeTarget,
    });

    [Fact]
    public void SetChoices_DefaultsToTheFirstColumn_AndFormatsTargets()
    {
        _panel.EditColumnId.Should().Be(1);
        _panel.MergeTargets.Select(t => t.Display).Should().Equal("請求先情報を更新する（未着手）", "週次レポートを作成する（進行中）");
    }

    [Fact]
    public void Show_FillsTheEditorFromTheCandidate()
    {
        _panel.Show(Candidate(), index: 1, count: 3);

        _panel.EditTitle.Should().Be("請求先情報を更新する");
        _panel.EditDueDate.Should().Be(new DateTime(2026, 9, 8));
        _panel.EditProjectName.Should().Be("顧客A");
        _panel.PositionText.Should().Be("2 / 3");
    }

    [Fact]
    public void Show_PreselectsTheSuggestedMergeTarget_WhenItIsOnTheBoard()
    {
        _panel.Show(Candidate(mergeTarget: 12), 0, 1);

        _panel.EditMergeTargetId.Should().Be(12);
        _panel.CanMerge.Should().BeTrue();
    }

    [Fact]
    public void Show_LeavesTheTargetUnselected_WhenTheSuggestionIsGone()
    {
        _panel.Show(Candidate(mergeTarget: 999), 0, 1);

        _panel.EditMergeTargetId.Should().BeNull("推薦されたタスクが盤面に無ければ選ばせるだけ");
        _panel.CanMerge.Should().BeFalse();
        _panel.MergeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_UsesTheTargetThePersonChose()
    {
        _panel.Show(Candidate(), 0, 1);
        _panel.EditMergeTargetId = 10;

        await _panel.MergeCommand.ExecuteAsync(null);

        _service.MergeCalls.Should().ContainSingle().Which.Should().Be(new MergeCall(1, 10));
        _decisions.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_DoesNothingWithoutATarget()
    {
        _panel.Show(Candidate(), 0, 1);

        await _panel.MergeCommand.ExecuteAsync(null);

        _service.MergeCalls.Should().BeEmpty();
        _decisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Register_PassesTheEditedValues()
    {
        _panel.Show(Candidate(), 0, 1);
        _panel.EditTitle = "書き換えた題名";
        _panel.EditDueDate = new DateTime(2026, 9, 10);
        _panel.EditProjectName = "別プロジェクト";
        _panel.EditColumnId = 2;

        await _panel.RegisterCommand.ExecuteAsync(null);

        _service.RegisterCalls.Should().ContainSingle().Which.Should()
            .Be(new CandidateDecision(1, "書き換えた題名", new DateOnly(2026, 9, 10), "別プロジェクト", 2));
    }

    [Fact]
    public async Task RunAsync_Merge_DoesNothingWithoutATarget()
    {
        _panel.Show(Candidate(), 0, 1);

        await _panel.RunAsync(TriageKeyAction.Merge);

        _service.MergeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_Reject_CallsTheService()
    {
        _panel.Show(Candidate(), 0, 1);

        await _panel.RunAsync(TriageKeyAction.Reject);

        _service.RejectCalls.Should().ContainSingle().Which.Should().Be(1);
        _decisions.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_Reject_IgnoresARepeatWhileTheFirstCallIsStillRunning()
    {
        var tcs = new TaskCompletionSource<Result>();
        _service.OnReject = _ => tcs.Task;
        _panel.Show(Candidate(), 0, 1);

        var first = _panel.RunAsync(TriageKeyAction.Reject);
        var second = _panel.RunAsync(TriageKeyAction.Reject);
        tcs.SetResult(Result.Ok());
        await Task.WhenAll(first, second);

        _service.RejectCalls.Should().ContainSingle().Which.Should().Be(1);
        _decisions.Should().ContainSingle();
    }

    [Fact]
    public void OpenLink_OpensTheCandidateLink()
    {
        _panel.Show(Candidate(), 0, 1);

        _panel.OpenLinkCommand.Execute(null);

        _opened.Should().Equal("https://outlook.office.com/x");
    }
}
