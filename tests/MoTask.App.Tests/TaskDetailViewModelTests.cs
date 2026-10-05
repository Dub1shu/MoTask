using System.Globalization;
using FluentAssertions;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class TaskDetailViewModelTests
{
    private readonly FakeBoardService _service = new();
    private readonly Board _board;
    private readonly BoardViewModel _vm;

    public TaskDetailViewModelTests()
    {
        _board = TestBoards.Sample();
        _service.OnGetBoard = () => Task.FromResult(Result.Ok(_board));
        _service.Projects = new[] { TestBoards.ProjectA() };
        _service.Labels = new[] { TestBoards.Urgent() };
        _service.OnGetHistory = _ => Task.FromResult<IReadOnlyList<HistoryEntry>>(new[]
        {
            new HistoryEntry { TaskId = 10, At = DateTime.UtcNow, Kind = HistoryKind.Created, ToColumnId = 1 },
        });
        _vm = new BoardViewModel(_service, new TestClock(), new FakeAiJobService(), new FakeBoardChangeSource());
    }

    private async Task<TaskDetailViewModel> OpenAsync(int taskId)
    {
        await _vm.LoadAsync();
        var card = _vm.Columns.SelectMany(c => c.AllCards).Single(c => c.Id == taskId);
        _vm.SelectCard(card);
        var detail = _vm.Detail!;
        await detail.PendingSave;
        return detail;
    }

    [Fact]
    public async Task Open_LoadsFieldsAndHistory()
    {
        var detail = await OpenAsync(10);

        detail.Title.Should().Be("請求先情報を更新する");
        detail.SelectedProject!.Name.Should().Be("顧客A対応");
        detail.DueDate.Should().Be(new DateTime(2026, 9, 8));
        detail.SelectedColumn!.Id.Should().Be(1);
        detail.Labels.Single().IsSelected.Should().BeTrue();
        detail.History.Should().ContainSingle().Which.Should().EndWith("未着手 に作成");
        _service.UpdateTaskCalls.Should().BeEmpty();
    }

    /// <summary>
    /// F1: 壊れた Detail JSON を持つ履歴行が1件あるだけで、コンストラクタが代入する PendingSave が
    /// 例外を握ったまま fault してはいけない（GetHistoryAsync の DB 失敗と同じくバナーではなく
    /// フォールバック文言で表に出す）。
    /// </summary>
    [Fact]
    public async Task Open_WithCorruptHistoryDetail_DoesNotFaultAndShowsFallback()
    {
        _service.OnGetHistory = _ => Task.FromResult<IReadOnlyList<HistoryEntry>>(new[]
        {
            new HistoryEntry { TaskId = 10, At = DateTime.UtcNow, Kind = HistoryKind.Edited, Detail = "{not valid json" },
        });

        var detail = await OpenAsync(10); // PendingSave が fault していればここで例外が飛ぶ

        detail.PendingSave.IsFaulted.Should().BeFalse();
        detail.History.Should().ContainSingle().Which.Should().EndWith(MoTask.App.Resources.Strings.HistoryUnknown);
    }

    [Fact]
    public async Task ChangingTitle_SavesExactlyOnce()
    {
        var detail = await OpenAsync(10);

        detail.Title = "新しい題";
        await detail.PendingSave;

        var update = _service.UpdateTaskCalls.Should().ContainSingle().Which.Update;
        update.TaskId.Should().Be(10);
        update.Title.Should().Be("新しい題");
        update.ProjectId.Should().Be(100);
        update.DueDate.Should().Be(new DateOnly(2026, 9, 8));
    }

    [Fact]
    public async Task SettingSameTitle_DoesNotSave()
    {
        var detail = await OpenAsync(10);

        detail.Title = "請求先情報を更新する";
        await detail.PendingSave;

        _service.UpdateTaskCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyTitle_IsRejectedLocally()
    {
        var detail = await OpenAsync(10);

        detail.Title = "   ";
        await detail.PendingSave;

        detail.HasTitleError.Should().BeTrue();
        _service.UpdateTaskCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangingColumn_MovesToEndOfThatColumn()
    {
        var detail = await OpenAsync(10);

        detail.SelectedColumn = _vm.Columns[1];
        await detail.PendingSave;

        // int.MaxValue は移動先の件数（1）に丸められてから BoardService へ渡る
        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 2, 1));
    }

    /// <summary>
    /// 詳細パネルを開いたままカードをボード側で動かす（＝カードのドラッグ＆ドロップ）と、
    /// 履歴は自分の操作で動かしたときと同じように更新されなければならない。
    /// </summary>
    [Fact]
    public async Task MovingCardFromTheBoard_RefreshesHistory()
    {
        var backlog = _board.Columns[0];
        var done = _board.Columns[2];
        var history = new List<HistoryEntry>
        {
            new() { TaskId = 10, At = new DateTime(2026, 9, 4, 8, 40, 0, DateTimeKind.Utc), Kind = HistoryKind.Created, ToColumnId = 1 },
        };
        _service.OnGetHistory = call => call.TaskId == 10
            ? Task.FromResult<IReadOnlyList<HistoryEntry>>(history.OrderByDescending(e => e.At).ToList())
            : Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>());
        _service.OnMoveTask = call =>
        {
            if (call is { TaskId: 10, ToColumnId: 3 })
            {
                TestBoards.Move(backlog, done, taskId: 10, position: 0);
                history.Add(new HistoryEntry
                {
                    TaskId = 10, At = new DateTime(2026, 9, 4, 9, 15, 0, DateTimeKind.Utc),
                    Kind = HistoryKind.Moved, FromColumnId = 1, ToColumnId = 3,
                });
            }
            return Task.FromResult(Result.Ok());
        };
        var detail = await OpenAsync(10);
        detail.History.Should().ContainSingle();

        await _vm.MoveCardAsync(detail.Card, _vm.Columns[2], 0);
        await detail.PendingSave;

        detail.SelectedColumn!.Id.Should().Be(3);
        detail.History.Should().HaveCount(2);
        detail.History[0].Should().EndWith("未着手 → 完了");
    }

    // ---------- 「完了にする」ボタン ----------

    /// <summary>ボタンは列の ComboBox を触らせずに完了列へ移す。</summary>
    [Fact]
    public async Task Complete_MovesTheTaskToTheDoneColumn()
    {
        MoveToDoneOnRequest();
        var detail = await OpenAsync(10);

        await detail.CompleteCommand.ExecuteAsync(null);
        await detail.PendingSave;

        _service.MoveTaskCalls.Should().ContainSingle().Which.Should().Be(new MoveTaskCall(10, 3, 0));
        detail.SelectedColumn!.Id.Should().Be(3);
    }

    /// <summary>完了列に入ったらボタンを消す（CanComplete が表示条件）。</summary>
    [Fact]
    public async Task CanComplete_TurnsFalse_OnceTheTaskIsDone()
    {
        MoveToDoneOnRequest();
        var detail = await OpenAsync(10);
        detail.CanComplete.Should().BeTrue();

        await detail.CompleteCommand.ExecuteAsync(null);
        await detail.PendingSave;

        detail.CanComplete.Should().BeFalse();
    }

    /// <summary>削除済みのタスクでは「削除」が「復元」に変わる段なので、完了も出さない。</summary>
    [Fact]
    public async Task CanComplete_IsFalse_ForADeletedTask()
    {
        TestBoards.SeedDeletedInTheMiddle(_board.Columns[0]);
        var detail = await OpenAsync(11);

        detail.IsDeleted.Should().BeTrue();
        detail.CanComplete.Should().BeFalse();
    }

    /// <summary>タスク 10 を完了列へ移す要求が来たら、実サービスと同じ副作用をモデルへ写す。</summary>
    private void MoveToDoneOnRequest()
    {
        var backlog = _board.Columns[0];
        var done = _board.Columns[2];
        _service.OnMoveTask = call =>
        {
            if (call is { TaskId: 10, ToColumnId: 3 }) TestBoards.Move(backlog, done, taskId: 10, position: call.Position);
            return Task.FromResult(Result.Ok());
        };
    }

    /// <summary>完了列に入るまでは完了日時の行を出さない。</summary>
    [Fact]
    public async Task CompletedAtText_IsNull_WhileTheTaskIsNotDone()
    {
        var detail = await OpenAsync(10);

        detail.CompletedAtText.Should().BeNull();
    }

    /// <summary>完了日時は履歴と同じ時計（UTC を現地時刻へ）で、同じ書式で出す。</summary>
    [Fact]
    public async Task CompletedAtText_ShowsTheLocalCompletionTime()
    {
        var completed = new DateTime(2026, 9, 4, 23, 41, 29, DateTimeKind.Utc);
        _board.Columns[0].Tasks.Single(t => t.Id == 10).CompletedAt = completed;
        var detail = await OpenAsync(10);

        var expected = TimeZoneInfo.ConvertTimeFromUtc(completed, TimeZoneInfo.Local)
            .ToString(MoTask.App.Resources.Strings.HistoryTimestampFormat, CultureInfo.InvariantCulture);
        detail.CompletedAtText.Should().Be(expected);
    }

    [Fact]
    public async Task TogglingLabel_CallsSetTaskLabels()
    {
        var detail = await OpenAsync(10);

        detail.Labels[0].IsSelected = false;
        await detail.PendingSave;

        var labels = _service.SetTaskLabelsCalls.Should().ContainSingle().Which;
        labels.TaskId.Should().Be(10);
        labels.LabelIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ThenRestore_UpdatesIsDeleted()
    {
        var task = _board.Columns[0].Tasks[0];
        _service.OnDeleteTask = taskId =>
        {
            if (taskId == 10) task.DeletedAt = DateTime.UtcNow;
            return Task.FromResult(Result.Ok());
        };
        _service.OnRestoreTask = taskId =>
        {
            if (taskId == 10) task.DeletedAt = null;
            return Task.FromResult(Result.Ok());
        };
        var detail = await OpenAsync(10);

        await detail.DeleteCommand.ExecuteAsync(null);
        detail.IsDeleted.Should().BeTrue();
        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(new[] { 11 }, "削除済みは既定で隠れる");

        await detail.RestoreCommand.ExecuteAsync(null);
        detail.IsDeleted.Should().BeFalse();
        _vm.Columns[0].Cards.Select(c => c.Id).Should().Equal(10, 11);
    }

    [Fact]
    public async Task Close_ClearsDetail()
    {
        var detail = await OpenAsync(10);
        detail.CloseCommand.Execute(null);
        _vm.Detail.Should().BeNull();
        _vm.SelectedCard.Should().BeNull();
    }

    // ---- 新しいプロジェクト／ラベルの入力欄は「＋」で開いたときだけ出す ----

    [Fact]
    public async Task AddProject_IsClosedUntilBegun()
    {
        var detail = await OpenAsync(10);
        detail.IsAddingProject.Should().BeFalse();

        detail.BeginAddProjectCommand.Execute(null);

        detail.IsAddingProject.Should().BeTrue();
    }

    [Fact]
    public async Task CreateProject_AssignsTheNewProject_AndCloses()
    {
        _service.OnCreateProject = name =>
        {
            var created = new Project { Id = 200, Name = name };
            _service.Projects = _service.Projects.Append(created).ToList();
            return Task.FromResult(Result.Ok(created));
        };
        var detail = await OpenAsync(10);
        detail.BeginAddProjectCommand.Execute(null);
        detail.NewProjectName = "新案件";

        await detail.CreateProjectCommand.ExecuteAsync(null);
        await detail.PendingSave;

        detail.IsAddingProject.Should().BeFalse();
        detail.NewProjectName.Should().BeEmpty();
        _service.UpdateTaskCalls.Should().ContainSingle().Which.Update.ProjectId.Should().Be(200);
    }

    [Fact]
    public async Task CreateProject_WithEmptyName_StaysOpen()
    {
        var detail = await OpenAsync(10);
        detail.BeginAddProjectCommand.Execute(null);

        await detail.CreateProjectCommand.ExecuteAsync(null);

        detail.IsAddingProject.Should().BeTrue();
        _service.CreateProjectCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateProject_WhenRejected_StaysOpenWithTheInput()
    {
        _service.OnCreateProject = _ => Task.FromResult(Result.Fail<Project>("同名のプロジェクトがあります"));
        var detail = await OpenAsync(10);
        detail.BeginAddProjectCommand.Execute(null);
        detail.NewProjectName = "顧客A対応";

        await detail.CreateProjectCommand.ExecuteAsync(null);

        detail.IsAddingProject.Should().BeTrue();
        detail.NewProjectName.Should().Be("顧客A対応");
    }

    [Fact]
    public async Task CancelAddProject_DiscardsTheInput_AndCloses()
    {
        var detail = await OpenAsync(10);
        detail.BeginAddProjectCommand.Execute(null);
        detail.NewProjectName = "書きかけ";

        detail.CancelAddProjectCommand.Execute(null);

        detail.IsAddingProject.Should().BeFalse();
        detail.NewProjectName.Should().BeEmpty();
        _service.CreateProjectCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AddLabel_IsClosedUntilBegun()
    {
        var detail = await OpenAsync(10);
        detail.IsAddingLabel.Should().BeFalse();

        detail.BeginAddLabelCommand.Execute(null);

        detail.IsAddingLabel.Should().BeTrue();
    }

    [Fact]
    public async Task CreateLabel_AttachesTheNewLabel_AndCloses()
    {
        _service.OnCreateLabel = call => Task.FromResult(Result.Ok(new Label { Id = 300, Name = call.Name, Color = call.Color }));
        var detail = await OpenAsync(10);
        detail.BeginAddLabelCommand.Execute(null);
        detail.NewLabelName = "定例";

        await detail.CreateLabelCommand.ExecuteAsync(null);

        detail.IsAddingLabel.Should().BeFalse();
        detail.NewLabelName.Should().BeEmpty();
        _service.SetTaskLabelsCalls.Should().ContainSingle().Which.LabelIds.Should().Contain(300);
    }

    [Fact]
    public async Task CancelAddLabel_DiscardsTheInput_AndCloses()
    {
        var detail = await OpenAsync(10);
        detail.BeginAddLabelCommand.Execute(null);
        detail.NewLabelName = "書きかけ";

        detail.CancelAddLabelCommand.Execute(null);

        detail.IsAddingLabel.Should().BeFalse();
        detail.NewLabelName.Should().BeEmpty();
        _service.CreateLabelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateLabel_ExistingAttachedName_DoesNotDuplicateTheId()
    {
        // サービスは同名の既存ラベル（すでに付いている至急）を返す
        _service.OnCreateLabel = _ => Task.FromResult(Result.Ok(TestBoards.Urgent()));
        var detail = await OpenAsync(10);
        detail.BeginAddLabelCommand.Execute(null);
        detail.NewLabelName = "至急";

        await detail.CreateLabelCommand.ExecuteAsync(null);

        _service.SetTaskLabelsCalls.Should().ContainSingle()
            .Which.LabelIds.Should().OnlyHaveUniqueItems().And.Contain(200);
    }

    /// <summary>
    /// 管理ダイアログからの作成・復元は詳細パネルを通らない。開いている詳細パネルの選択肢にも、その場で出ること。
    /// </summary>
    [Fact]
    public async Task CreatingFromElsewhere_ShowsUpInTheOpenDetailPanel()
    {
        var created = new Label { Id = 300, Name = "定例", Color = "green-300" };
        var project = new Project { Id = 102, Name = "新案件" };
        _service.OnCreateLabel = _ => Task.FromResult(Result.Ok(created));
        _service.OnCreateProject = _ => Task.FromResult(Result.Ok(project));
        var detail = await OpenAsync(10);
        _service.Labels = new[] { TestBoards.Urgent(), created };
        _service.Projects = new[] { TestBoards.ProjectA(), project };

        await _vm.CreateLabelAsync("定例");
        await _vm.CreateProjectAsync("新案件");

        detail.Labels.Select(l => l.Id).Should().Contain(300);
        detail.Projects.Select(p => p.Id).Should().Contain(102);
    }

    [Fact]
    public async Task Detail_ListsLabelsInDisplayOrder_IncludingArchivedOnesTheTaskHas()
    {
        _service.Projects = new[]
        {
            new Project { Id = 100, Name = "顧客A対応", Order = 1 },
            new Project { Id = 101, Name = "B案件", Order = 0 },
        };
        _service.Labels = new[]
        {
            new Label { Id = 200, Name = "至急", Color = "accent-500", Order = 1, Archived = true },
            new Label { Id = 201, Name = "z", Order = 2 },
            new Label { Id = 202, Name = "y", Order = 0 },
        };

        var detail = await OpenAsync(10);

        detail.Projects.Skip(1).Select(p => p.Name).Should().Equal("B案件", "顧客A対応"); // 先頭は「なし」
        detail.Labels.Select(l => l.Name).Should().Equal("y", "至急", "z");
    }
}
