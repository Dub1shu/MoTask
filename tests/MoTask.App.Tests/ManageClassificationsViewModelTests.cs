using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class ManageClassificationsViewModelTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Board _board;
    private readonly Project _projectA = TestBoards.ProjectA();
    private readonly Project _unused = new() { Id = 101, Name = "使っていない案件" };
    private readonly Label _urgent = TestBoards.Urgent();
    private readonly Label _spare = new() { Id = 201, Name = "予備", Color = "accent-300" };
    private readonly BoardViewModel _vm;

    public ManageClassificationsViewModelTests()
    {
        _board = TestBoards.Sample(_urgent);
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Project>>(new[] { _projectA, _unused }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Label>>(new[] { _urgent, _spare }));
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>()));
        _service.ArchiveProjectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _projectA.Archived = true;
            return Task.FromResult(Result.Ok());
        });
        _service.UnarchiveProjectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _projectA.Archived = false;
            return Task.FromResult(Result.Ok());
        });
        _service.ArchiveLabelAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _urgent.Archived = true;
            return Task.FromResult(Result.Ok());
        });
        _service.UnarchiveLabelAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _urgent.Archived = false;
            return Task.FromResult(Result.Ok());
        });
        _vm = new BoardViewModel(_service, new TestClock(), Substitute.For<IAiJobService>(), Substitute.For<IBoardChangeSource>());
    }

    private async Task<ManageClassificationsViewModel> OpenAsync()
    {
        await _vm.LoadAsync();
        return new ManageClassificationsViewModel(_vm);
    }

    /// <summary>使用件数は読み込み済みのボードから数える。論理削除済みのタスクは含めない。</summary>
    [Fact]
    public async Task Rows_ListProjectsThenLabels_WithUsageCounts()
    {
        var manage = await OpenAsync();

        manage.Projects.Select(r => r.Name).Should().BeEquivalentTo(new[] { "顧客A対応", "使っていない案件" });
        manage.Projects.Single(r => r.Id == 100).UsageCount.Should().Be(1);
        manage.Projects.Single(r => r.Id == 101).UsageCount.Should().Be(0);

        // 名前の並び順は日本語の照合順序に依存するので、順序ではなく中身で確かめる
        manage.Labels.Select(r => r.Name).Should().BeEquivalentTo(new[] { "至急", "予備" });
        manage.Labels.Single(r => r.Id == 200).UsageCount.Should().Be(1);
        manage.Labels.Single(r => r.Id == 201).UsageCount.Should().Be(0);
    }

    [Fact]
    public async Task UsageCount_ExcludesDeletedTasks()
    {
        _board.Columns[0].Tasks.Single(t => t.Id == 10).DeletedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

        var manage = await OpenAsync();

        manage.Projects.Single(r => r.Id == 100).UsageCount.Should().Be(0);
        manage.Labels.Single(r => r.Id == 200).UsageCount.Should().Be(0);
    }

    [Fact]
    public async Task ArchivingAProject_RemovesItFromTheFilterButKeepsItOnTheTask()
    {
        var manage = await OpenAsync();

        await manage.ArchiveProjectAsync(manage.Projects.Single(r => r.Id == 100));

        await _service.Received(1).ArchiveProjectAsync(100, Arg.Any<CancellationToken>());
        _vm.Filter.Projects.Select(o => o.Id).Should().NotContain(100);
        _board.Columns[0].Tasks.Single(t => t.Id == 10).ProjectId.Should().Be(100);
        manage.Projects.Single(r => r.Id == 100).IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task RestoringAProject_BringsItBackToTheFilter()
    {
        var manage = await OpenAsync();
        var row = manage.Projects.Single(r => r.Id == 100);
        await manage.ArchiveProjectAsync(row);

        await manage.UnarchiveProjectAsync(manage.Projects.Single(r => r.Id == 100));

        await _service.Received(1).UnarchiveProjectAsync(100, Arg.Any<CancellationToken>());
        _vm.Filter.Projects.Select(o => o.Id).Should().Contain(100);
        manage.Projects.Single(r => r.Id == 100).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ArchivingALabel_RemovesItFromTheFilterButKeepsItOnTheTask()
    {
        var manage = await OpenAsync();

        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        await _service.Received(1).ArchiveLabelAsync(200, Arg.Any<CancellationToken>());
        _vm.Filter.Labels.Select(l => l.Id).Should().NotContain(200);
        _board.Columns[0].Tasks.Single(t => t.Id == 10).Labels.Should().ContainSingle().Which.Id.Should().Be(200);
        manage.Labels.Single(r => r.Id == 200).IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task RestoringALabel_BringsItBackToTheFilter()
    {
        var manage = await OpenAsync();
        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        await manage.UnarchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        await _service.Received(1).UnarchiveLabelAsync(200, Arg.Any<CancellationToken>());
        _vm.Filter.Labels.Select(l => l.Id).Should().Contain(200);
    }

    /// <summary>
    /// 絞り込みに使っていたラベルをアーカイブすると条件そのものが消えるので、
    /// 隠れていたカードが出てくる。表示を絞り直さないとボードが嘘をつく。
    /// </summary>
    [Fact]
    public async Task ArchivingALabelUsedByTheFilter_WidensTheVisibleCards()
    {
        var manage = await OpenAsync();
        _vm.Filter.Labels.Single(l => l.Id == 200).IsSelected = true;
        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(10);

        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(10, 11);
    }

    /// <summary>
    /// 詳細パネルを開いたままアーカイブしたら、ラベルのトグル一覧からも退く。
    /// ただしそのタスクが既に持っているラベルは残す（プロジェクトの既存挙動に合わせる）。
    /// </summary>
    [Fact]
    public async Task ArchivingALabel_RefreshesTheOpenDetailPanel()
    {
        var manage = await OpenAsync();
        var card = _vm.Columns[0].AllCards.Single(c => c.Id == 11); // 至急 を持たないタスク
        _vm.SelectCard(card);
        var detail = _vm.Detail!;
        await detail.PendingSave;
        detail.Labels.Select(l => l.Id).Should().Contain(200);

        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        detail.Labels.Select(l => l.Id).Should().NotContain(200);
    }

    [Fact]
    public async Task ArchivingALabel_KeepsItOnTheDetailPanelOfATaskThatUsesIt()
    {
        var manage = await OpenAsync();
        var card = _vm.Columns[0].AllCards.Single(c => c.Id == 10); // 至急 を持つタスク
        _vm.SelectCard(card);
        var detail = _vm.Detail!;
        await detail.PendingSave;

        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        detail.Labels.Select(l => l.Id).Should().Contain(200);
    }
}
