using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class ManageClassificationsViewModelTests
{
    private readonly FakeBoardService _service = new();
    private readonly Board _board;
    private readonly Project _projectA = TestBoards.ProjectA();
    private readonly Project _unused = new() { Id = 101, Name = "使っていない案件" };
    private readonly Label _urgent = TestBoards.Urgent();
    private readonly Label _spare = new() { Id = 201, Name = "予備", Color = "accent-300" };
    private readonly BoardViewModel _vm;

    public ManageClassificationsViewModelTests()
    {
        _board = TestBoards.Sample(_urgent);
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.OnGetProjects = () => Task.FromResult<IReadOnlyList<Project>>(new[] { _projectA, _unused });
        _service.OnGetLabels = () => Task.FromResult<IReadOnlyList<Label>>(new[] { _urgent, _spare });
        _service.OnArchiveProject = _ =>
        {
            _projectA.Archived = true;
            return Task.FromResult(Result.Ok());
        };
        _service.OnUnarchiveProject = _ =>
        {
            _projectA.Archived = false;
            return Task.FromResult(Result.Ok());
        };
        _service.OnArchiveLabel = _ =>
        {
            _urgent.Archived = true;
            return Task.FromResult(Result.Ok());
        };
        _service.OnUnarchiveLabel = _ =>
        {
            _urgent.Archived = false;
            return Task.FromResult(Result.Ok());
        };
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), new FakeBoardChangeSource());
    }

    private async Task<ManageClassificationsViewModel> OpenAsync()
    {
        await _vm.LoadAsync();
        return new ManageClassificationsViewModel(_vm, ClassificationKind.Project, DefaultFolder);
    }

    private const string DefaultFolder = @"C:\Users\me\MoTask";

    /// <summary>プロジェクト設定とラベル設定は同じダイアログを種別で出し分ける。題名と出す一覧が種別に従う。</summary>
    [Theory]
    [InlineData(ClassificationKind.Project, true)]
    [InlineData(ClassificationKind.Label, false)]
    public async Task Kind_DecidesTitleAndShownList(ClassificationKind kind, bool isProjects)
    {
        await _vm.LoadAsync();
        var manage = new ManageClassificationsViewModel(_vm, kind, DefaultFolder);

        manage.IsProjects.Should().Be(isProjects);
        manage.IsLabels.Should().Be(!isProjects);
        manage.Title.Should().Be(isProjects ? Strings.ManageProjects : Strings.ManageLabels);
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

        _service.ArchiveProjectCalls.Should().ContainSingle().Which.Should().Be(100);
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

        _service.UnarchiveProjectCalls.Should().ContainSingle().Which.Should().Be(100);
        _vm.Filter.Projects.Select(o => o.Id).Should().Contain(100);
        manage.Projects.Single(r => r.Id == 100).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ArchivingALabel_RemovesItFromTheFilterButKeepsItOnTheTask()
    {
        var manage = await OpenAsync();

        await manage.ArchiveLabelAsync(manage.Labels.Single(r => r.Id == 200));

        _service.ArchiveLabelCalls.Should().ContainSingle().Which.Should().Be(200);
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

        _service.UnarchiveLabelCalls.Should().ContainSingle().Which.Should().Be(200);
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

    [Fact]
    public async Task Rename_Commit_CallsServiceAndRefreshes()
    {
        _service.OnRenameLabel = call =>
        {
            _urgent.Name = call.Name;
            return Task.FromResult(Result.Ok());
        };
        var manage = await OpenAsync();
        var row = manage.Labels.Single(r => r.Id == 200);

        manage.BeginRenameCommand.Execute(row);
        row.IsEditing.Should().BeTrue();
        row.EditName.Should().Be("至急");
        row.EditName = " 大至急 ";
        manage.CommitRenameCommand.Execute(row);
        await manage.PendingChange;

        _service.RenameLabelCalls.Should().ContainSingle().Which.Should().Be(new RenameCall(200, "大至急"));
        manage.Labels.Single(r => r.Id == 200).Name.Should().Be("大至急");
        manage.Labels.Single(r => r.Id == 200).IsEditing.Should().BeFalse();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(" 至急 ")]
    public async Task Rename_EmptyOrUnchanged_JustCloses(string input)
    {
        var manage = await OpenAsync();
        var row = manage.Labels.Single(r => r.Id == 200);

        manage.BeginRenameCommand.Execute(row);
        row.EditName = input;
        manage.CommitRenameCommand.Execute(row);
        await manage.PendingChange;

        _service.RenameLabelCalls.Should().BeEmpty();
        row.IsEditing.Should().BeFalse();
        row.Name.Should().Be("至急");
    }

    [Fact]
    public async Task Rename_Cancel_KeepsTheName()
    {
        var manage = await OpenAsync();
        var row = manage.Projects.Single(r => r.Id == 100);

        manage.BeginRenameCommand.Execute(row);
        row.EditName = "別名";
        manage.CancelRenameCommand.Execute(row);

        row.IsEditing.Should().BeFalse();
        row.Name.Should().Be("顧客A対応");
        _service.RenameProjectCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Rename_Failure_ShowsErrorAndKeepsTheName_ThenClearsOnNextAction()
    {
        _service.OnRenameProject = _ => Task.FromResult(Result.Fail(Messages.ProjectNameDuplicate));
        var manage = await OpenAsync();
        var row = manage.Projects.Single(r => r.Id == 100);

        manage.BeginRenameCommand.Execute(row);
        row.EditName = "使っていない案件";
        manage.CommitRenameCommand.Execute(row);
        await manage.PendingChange;

        manage.ErrorMessage.Should().Be(Messages.ProjectNameDuplicate);
        manage.Projects.Single(r => r.Id == 100).Name.Should().Be("顧客A対応");
        manage.Projects.Single(r => r.Id == 100).IsEditing.Should().BeFalse();

        manage.BeginRenameCommand.Execute(manage.Projects.Single(r => r.Id == 100));
        manage.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Swatches_MarkTheCurrentColor_AndNoneForOffPaletteColors()
    {
        _urgent.Color = "red-600";
        _spare.Color = "accent-500"; // 見本の外（以前の自動の色）
        var manage = await OpenAsync();

        var urgent = manage.Labels.Single(r => r.Id == 200);
        urgent.Swatches.Should().HaveCount(18);
        urgent.Swatches.Single(s => s.IsSelected).Color.Should().Be("red-600");
        manage.Labels.Single(r => r.Id == 201).Swatches.Should().OnlyContain(s => !s.IsSelected);
    }

    [Fact]
    public async Task SetColor_CallsServiceAndRefreshes()
    {
        _service.OnSetLabelColor = call =>
        {
            _urgent.Color = call.Color;
            return Task.FromResult(Result.Ok());
        };
        var manage = await OpenAsync();
        var swatch = manage.Labels.Single(r => r.Id == 200).Swatches.Single(s => s.Color == "teal-300");

        manage.SetColorCommand.Execute(swatch);
        await manage.PendingChange;

        _service.SetLabelColorCalls.Should().ContainSingle().Which.Should().Be(new SetLabelColorCall(200, "teal-300"));
        manage.Labels.Single(r => r.Id == 200).Color.Should().Be("teal-300");
    }

    [Fact]
    public async Task AddLabel_EmptyEnter_StaysOpen()
    {
        var manage = await OpenAsync();
        manage.BeginAddLabelCommand.Execute(null);
        manage.NewLabelName = "  ";

        manage.CreateLabelCommand.Execute(null);
        await manage.PendingChange;

        manage.IsAddingLabel.Should().BeTrue();
        _service.CreateLabelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AddProject_Success_ClosesAndAddsTheRow()
    {
        var created = new Project { Id = 102, Name = "新案件" };
        _service.OnCreateProject = _ => Task.FromResult(Result.Ok(created));
        _service.OnGetProjects = () => Task.FromResult<IReadOnlyList<Project>>(new[] { _projectA, _unused, created });
        var manage = await OpenAsync();
        manage.BeginAddProjectCommand.Execute(null);
        manage.NewProjectName = " 新案件 ";

        manage.CreateProjectCommand.Execute(null);
        await manage.PendingChange;

        _service.CreateProjectCalls.Should().ContainSingle().Which.Should().Be("新案件");
        manage.IsAddingProject.Should().BeFalse();
        manage.NewProjectName.Should().BeEmpty();
        manage.Projects.Select(r => r.Id).Should().Contain(102);
    }

    [Fact]
    public async Task AddLabel_SameNameAsArchived_RestoresInsteadOfAddingARow()
    {
        _urgent.Archived = true;
        _service.OnCreateLabel = _ =>
        {
            _urgent.Archived = false; // 実サービスは同名のアーカイブ済みを復元して返す
            return Task.FromResult(Result.Ok(_urgent));
        };
        var manage = await OpenAsync();
        manage.BeginAddLabelCommand.Execute(null);
        manage.NewLabelName = "至急";

        manage.CreateLabelCommand.Execute(null);
        await manage.PendingChange;

        manage.Labels.Should().HaveCount(2);
        manage.Labels.Single(r => r.Id == 200).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectRows_OfferThePaletteAndNoColor()
    {
        _projectA.Color = "green-300";
        var manage = await OpenAsync();

        var colored = manage.Projects.Single(r => r.Id == 100);
        colored.Color.Should().Be("green-300");
        colored.Swatches.Should().HaveCount(18);
        colored.Swatches.Single(s => s.IsSelected).Color.Should().Be("green-300");
        colored.NoColorSwatch.Should().NotBeNull();
        colored.NoColorSwatch!.IsSelected.Should().BeFalse();

        var uncolored = manage.Projects.Single(r => r.Id == 101);
        uncolored.Color.Should().BeNull();
        uncolored.Swatches.Should().OnlyContain(s => !s.IsSelected);
        uncolored.NoColorSwatch!.IsSelected.Should().BeTrue();

        manage.Labels.Single(r => r.Id == 200).NoColorSwatch.Should().BeNull("ラベルは必ず色を持つ");
    }

    [Fact]
    public async Task ProjectSetColor_AndClear_CallTheProjectService()
    {
        _projectA.Color = "green-300";
        _service.OnSetProjectColor = call =>
        {
            _projectA.Color = call.Color;
            return Task.FromResult(Result.Ok());
        };
        var manage = await OpenAsync();

        manage.SetColorCommand.Execute(manage.Projects.Single(r => r.Id == 100).Swatches.Single(s => s.Color == "pink-600"));
        await manage.PendingChange;
        manage.Projects.Single(r => r.Id == 100).Color.Should().Be("pink-600");

        manage.SetColorCommand.Execute(manage.Projects.Single(r => r.Id == 100).NoColorSwatch!);
        await manage.PendingChange;

        _service.SetProjectColorCalls.Should().Equal(new SetProjectColorCall(100, "pink-600"), new SetProjectColorCall(100, null));
        manage.Projects.Single(r => r.Id == 100).Color.Should().BeNull();
        _service.SetLabelColorCalls.Should().BeEmpty();
    }

    // ---------- 作業フォルダ ----------

    /// <summary>未設定のプロジェクトは既定のフォルダを添えて出す。ラベルの行にはフォルダが無い。</summary>
    [Fact]
    public async Task ProjectRows_ShowTheWorkingDirectory_OrTheDefault()
    {
        _projectA.WorkingDirectory = @"D:\repo\a";
        var manage = await OpenAsync();

        var set = manage.Projects.Single(r => r.Id == 100);
        set.HasWorkingDirectory.Should().BeTrue();
        set.WorkingDirectory.Should().Be(@"D:\repo\a");
        set.WorkingDirectoryText.Should().Be(@"D:\repo\a");

        var unset = manage.Projects.Single(r => r.Id == 101);
        unset.HasWorkingDirectory.Should().BeFalse();
        unset.WorkingDirectoryText.Should().Be(string.Format(Strings.ManageDefaultWorkingDirectoryFormat, DefaultFolder));

        manage.Labels.Should().OnlyContain(r => !r.IsProject);
        manage.Projects.Should().OnlyContain(r => r.IsProject);
    }

    [Fact]
    public async Task SetWorkingDirectory_AndReset_CallTheServiceAndRefresh()
    {
        _service.OnSetProjectWorkingDirectory = call =>
        {
            _projectA.WorkingDirectory = call.Path;
            return Task.FromResult(Result.Ok());
        };
        var manage = await OpenAsync();

        manage.SetWorkingDirectory(manage.Projects.Single(r => r.Id == 100), @"D:\repo\a");
        await manage.PendingChange;
        manage.Projects.Single(r => r.Id == 100).WorkingDirectory.Should().Be(@"D:\repo\a");

        manage.ClearWorkingDirectoryCommand.Execute(manage.Projects.Single(r => r.Id == 100));
        await manage.PendingChange;

        _service.SetProjectWorkingDirectoryCalls.Should().Equal(
            new SetProjectWorkingDirectoryCall(100, @"D:\repo\a"), new SetProjectWorkingDirectoryCall(100, null));
        manage.Projects.Single(r => r.Id == 100).HasWorkingDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task SetWorkingDirectory_SameFolder_DoesNothing()
    {
        _projectA.WorkingDirectory = @"D:\repo\a";
        var manage = await OpenAsync();

        manage.SetWorkingDirectory(manage.Projects.Single(r => r.Id == 100), @"D:\repo\a");
        await manage.PendingChange;

        _service.SetProjectWorkingDirectoryCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SetWorkingDirectory_Failure_ShowsTheError()
    {
        _service.OnSetProjectWorkingDirectory = _ => Task.FromResult(Result.Fail(Messages.ProjectNotFound));
        var manage = await OpenAsync();

        manage.SetWorkingDirectory(manage.Projects.Single(r => r.Id == 100), @"D:\repo\a");
        await manage.PendingChange;

        manage.ErrorMessage.Should().Contain(Messages.ProjectNotFound);
        manage.Projects.Single(r => r.Id == 100).HasWorkingDirectory.Should().BeFalse();
    }
}
