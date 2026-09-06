using System.Globalization;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class TaskDetailViewModelTests
{
    private readonly IBoardService _service = Substitute.For<IBoardService>();
    private readonly Board _board;
    private readonly BoardViewModel _vm;

    public TaskDetailViewModelTests()
    {
        _board = TestBoards.Sample();
        _service.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result.Ok(_board)));
        _service.GetProjectsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>(new[] { TestBoards.ProjectA() }));
        _service.GetLabelsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { TestBoards.Urgent() }));
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(new[]
            {
                new HistoryEntry { TaskId = 10, At = DateTime.UtcNow, Kind = HistoryKind.Created, ToColumnId = 1 },
            }));
        _service.UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _service.MoveTaskAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        _vm = new BoardViewModel(_service, new TestClock(), Substitute.For<IAiJobService>(), Substitute.For<IBoardChangeSource>());
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
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// F1: 壊れた Detail JSON を持つ履歴行が1件あるだけで、コンストラクタが代入する PendingSave が
    /// 例外を握ったまま fault してはいけない（GetHistoryAsync の DB 失敗と同じくバナーではなく
    /// フォールバック文言で表に出す）。
    /// </summary>
    [Fact]
    public async Task Open_WithCorruptHistoryDetail_DoesNotFaultAndShowsFallback()
    {
        _service.GetHistoryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEntry>>(new[]
            {
                new HistoryEntry { TaskId = 10, At = DateTime.UtcNow, Kind = HistoryKind.Edited, Detail = "{not valid json" },
            }));

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

        await _service.Received(1).UpdateTaskAsync(
            Arg.Is<TaskUpdate>(u => u.TaskId == 10 && u.Title == "新しい題" && u.ProjectId == 100
                                   && u.DueDate == new DateOnly(2026, 9, 8)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettingSameTitle_DoesNotSave()
    {
        var detail = await OpenAsync(10);

        detail.Title = "請求先情報を更新する";
        await detail.PendingSave;

        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyTitle_IsRejectedLocally()
    {
        var detail = await OpenAsync(10);

        detail.Title = "   ";
        await detail.PendingSave;

        detail.HasTitleError.Should().BeTrue();
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingColumn_MovesToEndOfThatColumn()
    {
        var detail = await OpenAsync(10);

        detail.SelectedColumn = _vm.Columns[1];
        await detail.PendingSave;

        // int.MaxValue は移動先の件数（1）に丸められてから BoardService へ渡る
        await _service.Received(1).MoveTaskAsync(10, 2, 1, Arg.Any<CancellationToken>());
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
        _service.GetHistoryAsync(10, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<HistoryEntry>>(history.OrderByDescending(e => e.At).ToList()));
        _service.MoveTaskAsync(10, 3, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            TestBoards.Move(backlog, done, taskId: 10, position: 0);
            history.Add(new HistoryEntry
            {
                TaskId = 10, At = new DateTime(2026, 9, 4, 9, 15, 0, DateTimeKind.Utc),
                Kind = HistoryKind.Moved, FromColumnId = 1, ToColumnId = 3,
            });
            return Task.FromResult(Result.Ok());
        });
        var detail = await OpenAsync(10);
        detail.History.Should().ContainSingle();

        await _vm.MoveCardAsync(detail.Card, _vm.Columns[2], 0);
        await detail.PendingSave;

        detail.SelectedColumn!.Id.Should().Be(3);
        detail.History.Should().HaveCount(2);
        detail.History[0].Should().EndWith("未着手 → 完了");
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
        _service.SetTaskLabelsAsync(10, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        var detail = await OpenAsync(10);

        detail.Labels[0].IsSelected = false;
        await detail.PendingSave;

        await _service.Received(1).SetTaskLabelsAsync(10, Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_ThenRestore_UpdatesIsDeleted()
    {
        var task = _board.Columns[0].Tasks[0];
        _service.DeleteTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ => { task.DeletedAt = DateTime.UtcNow; return Task.FromResult(Result.Ok()); });
        _service.RestoreTaskAsync(10, Arg.Any<CancellationToken>()).Returns(_ => { task.DeletedAt = null; return Task.FromResult(Result.Ok()); });
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

    [Fact]
    public async Task ProjectWorkingDirectory_IsShownForTheSelectedProject_AndSavedOnChange()
    {
        _service.SetProjectWorkingDirectoryAsync(100, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        var detail = await OpenAsync(10);

        detail.HasProject.Should().BeTrue();
        detail.ProjectWorkingDirectory.Should().BeNull();

        detail.ProjectWorkingDirectory = @"C:\work\a";
        await detail.PendingSave;

        await _service.Received(1).SetProjectWorkingDirectoryAsync(100, @"C:\work\a", Arg.Any<CancellationToken>());
        await _service.DidNotReceive().UpdateTaskAsync(Arg.Any<TaskUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProjectWorkingDirectory_IsHiddenWithoutAProject()
    {
        var detail = await OpenAsync(11);
        detail.HasProject.Should().BeFalse();
    }
}
