using FluentAssertions;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class ArchiveViewModelTests
{
    // 今週は 9/14（月）から。先週は 9/7〜9/13、2 週前は 8/31〜9/6。
    private static readonly DateOnly Thursday = new(2026, 9, 17);
    private static readonly DateTime Created = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeBoardService _service = new();
    private readonly Board _board = TestBoards.Sample();
    private readonly ArchiveViewModel _vm;

    public ArchiveViewModelTests()
    {
        _service.Board = _board;
        _service.Projects = new[] { TestBoards.ProjectA() };
        _vm = new ArchiveViewModel(_service, new TestClock { Today = Thursday }) { TimeZone = TimeZoneInfo.Utc };
    }

    private TaskItem AddDone(int id, DateTime completedUtc)
    {
        var done = _board.Columns[2];
        var task = new TaskItem
        {
            Id = id, Title = $"完了 {id}", ColumnId = done.Id, Position = done.Tasks.Count,
            CreatedAt = Created, UpdatedAt = Created, CompletedAt = completedUtc,
        };
        done.Tasks.Add(task);
        return task;
    }

    private static DateTime Noon(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Load_GroupsArchivedTasksByWeek_NewestFirst()
    {
        AddDone(30, Noon(9, 8));
        AddDone(31, Noon(9, 12));
        AddDone(32, Noon(9, 2));
        AddDone(33, Noon(9, 15)); // 今週分はボードに出すので、ここには来ない

        await _vm.LoadAsync();

        _vm.Weeks.Select(w => w.Start).Should().Equal(new DateOnly(2026, 9, 7), new DateOnly(2026, 8, 31));
        _vm.Weeks[0].Items.Select(i => i.Id).Should().Equal(31, 30);
        _vm.Weeks[1].Items.Select(i => i.Id).Should().Equal(32);
        _vm.Weeks[0].Heading.Should().Be("9/7（月） 〜 9/13（日）");
        _vm.Weeks[0].CountText.Should().Be("2 件");
        _vm.IsEmpty.Should().BeFalse();
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Load_SkipsDeletedTasksAndOtherColumns()
    {
        AddDone(30, Noon(9, 8)).DeletedAt = Noon(9, 9);
        // 完了列以外は、仮に CompletedAt が残っていても拾わない。
        _board.Columns[0].Tasks.Single(t => t.Id == 10).CompletedAt = Noon(9, 8);

        await _vm.LoadAsync();

        _vm.Weeks.Should().BeEmpty();
        _vm.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Load_FillsRowAndDetailTexts()
    {
        var task = AddDone(31, Noon(9, 12));
        task.ProjectId = 100;
        task.Description = "送付済み";
        task.Labels.Add(TestBoards.Urgent());

        await _vm.LoadAsync();

        var item = _vm.Weeks[0].Items[0];
        item.Title.Should().Be("完了 31");
        item.ProjectName.Should().Be("顧客A対応");
        item.CompletedOnText.Should().Be("9/12（土）");
        item.CompletedAtText.Should().Be("9/12 12:00");
        item.CreatedAtText.Should().Be("9/1 0:00");
        item.Description.Should().Be("送付済み");
        item.HasDescription.Should().BeTrue();
        item.Labels.Select(l => l.Name).Should().Equal("至急");
        item.HasLabels.Should().BeTrue();
    }

    [Fact]
    public async Task Load_WithNothingArchived_IsEmpty()
    {
        await _vm.LoadAsync();

        _vm.Weeks.Should().BeEmpty();
        _vm.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Load_WhenBoardQueryFails_ShowsErrorAndStaysEmpty()
    {
        _service.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("読めません"));

        await _vm.LoadAsync();

        _vm.ErrorMessage.Should().Be("読めません");
        _vm.Weeks.Should().BeEmpty();
        _vm.IsEmpty.Should().BeFalse("読めなかったのに「まだありません」と出すと誤解させる");
    }

    [Fact]
    public async Task Load_WhenBoardQueryThrows_ShowsErrorWithoutThrowing()
    {
        _service.OnGetBoard = () => throw new InvalidOperationException("db locked");

        var act = () => _vm.LoadAsync();

        await act.Should().NotThrowAsync();
        _vm.ErrorMessage.Should().Contain("db locked");
    }

    [Fact]
    public async Task Load_AfterFailure_ClearsTheError()
    {
        _service.OnGetBoard = () => Task.FromResult(Result.Fail<Board>("読めません"));
        await _vm.LoadAsync();
        _service.OnGetBoard = null;

        await _vm.LoadAsync();

        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Select_MarksTheItemAndUnmarksThePrevious()
    {
        AddDone(30, Noon(9, 8));
        AddDone(31, Noon(9, 12));
        await _vm.LoadAsync();
        var first = _vm.Weeks[0].Items[0];
        var second = _vm.Weeks[0].Items[1];

        _vm.SelectCommand.Execute(first);
        _vm.SelectCommand.Execute(second);

        _vm.SelectedItem.Should().BeSameAs(second);
        first.IsSelected.Should().BeFalse();
        second.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Reload_KeepsSelectionById()
    {
        AddDone(31, Noon(9, 12));
        await _vm.LoadAsync();
        _vm.SelectCommand.Execute(_vm.Weeks[0].Items[0]);

        await _vm.LoadAsync();

        _vm.SelectedItem!.Id.Should().Be(31);
        _vm.SelectedItem.Should().BeSameAs(_vm.Weeks[0].Items[0]);
        _vm.SelectedItem.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Reload_DropsSelectionWhenItemIsGone()
    {
        var task = AddDone(31, Noon(9, 12));
        await _vm.LoadAsync();
        _vm.SelectCommand.Execute(_vm.Weeks[0].Items[0]);
        // ボードで未着手へ戻された（BoardService は CompletedAt を null に戻す）。
        _board.Columns[2].Tasks.Remove(task);
        task.CompletedAt = null;
        _board.Columns[0].Tasks.Add(task);

        await _vm.LoadAsync();

        _vm.SelectedItem.Should().BeNull();
    }

    [Fact]
    public async Task OverlappingLoads_DoNotOverwriteNewerDataWithStaleData()
    {
        var staleGate = new TaskCompletionSource();
        var stale = TestBoards.Sample();
        var fresh = TestBoards.Sample();
        fresh.Columns[2].Tasks.Add(new TaskItem
        {
            Id = 40, Title = "新しい方", ColumnId = 3, CreatedAt = Created, UpdatedAt = Created, CompletedAt = Noon(9, 8),
        });
        var calls = 0;
        _service.OnGetBoard = async () =>
        {
            if (++calls == 1)
            {
                await staleGate.Task;
                return Result.Ok(stale);
            }
            return Result.Ok(fresh);
        };

        var first = _vm.LoadAsync();
        await _vm.LoadAsync();
        staleGate.SetResult();
        await first;

        _vm.Weeks.SelectMany(w => w.Items).Select(i => i.Id).Should().Equal(40);
    }

    [Fact]
    public async Task Load_CarriesTheProjectColor()
    {
        var project = TestBoards.ProjectA();
        project.Color = "teal-300";
        _service.Projects = new[] { project };
        AddDone(31, Noon(9, 12)).ProjectId = 100;

        await _vm.LoadAsync();

        _vm.Weeks[0].Items[0].ProjectColor.Should().Be("teal-300");
    }
}
