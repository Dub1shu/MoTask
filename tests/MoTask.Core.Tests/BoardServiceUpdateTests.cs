using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceUpdateTests
{
    private readonly InMemoryStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly BoardService _service;
    private readonly Column _backlog;
    private readonly TaskItem _task;

    public BoardServiceUpdateTests()
    {
        _backlog = _store.SeedColumn("未着手", ColumnRole.Backlog);
        _store.SeedColumn("完了", ColumnRole.Done);
        _task = _store.SeedTask(_backlog, "元のタイトル");
        _service = new BoardService(_store, _store, _store, _clock, new OperationGate());
    }

    private TaskUpdate Unchanged() => new(_task.Id, _task.Title, _task.Description, _task.ProjectId, _task.DueDate);

    [Fact]
    public async Task Update_NoChanges_WritesNoHistoryAndDoesNotSave()
    {
        var result = await _service.UpdateTaskAsync(Unchanged());

        result.IsSuccess.Should().BeTrue();
        _store.History.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_OnlyChangedFields_AppearInDetail()
    {
        var project = _store.SeedProject("顧客A対応");
        _clock.UtcNow = new DateTime(2026, 9, 4, 1, 0, 0, DateTimeKind.Utc);

        var result = await _service.UpdateTaskAsync(Unchanged() with
        {
            Title = " 新しいタイトル ",
            ProjectId = project.Id,
            DueDate = new DateOnly(2026, 9, 10),
        });

        result.IsSuccess.Should().BeTrue();
        _task.Title.Should().Be("新しいタイトル");
        _task.ProjectId.Should().Be(project.Id);
        _task.DueDate.Should().Be(new DateOnly(2026, 9, 10));
        _task.UpdatedAt.Should().Be(_clock.UtcNow);

        var history = await _service.GetHistoryAsync(_task.Id);
        history.Should().ContainSingle();
        history[0].Kind.Should().Be(HistoryKind.Edited);
        var detail = HistoryDetail.Deserialize(history[0].Detail);
        detail.Keys.Should().BeEquivalentTo(new[] { "Title", "Project", "DueDate" });
        detail["Title"].Should().Be(new FieldChange("元のタイトル", "新しいタイトル"));
        detail["Project"].Should().Be(new FieldChange(null, "顧客A対応"));
        detail["DueDate"].Should().Be(new FieldChange(null, "2026-09-10"));
    }

    [Fact]
    public async Task Update_DescriptionOnly_RecordsDescription()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { Description = "詳細を書く" });

        result.IsSuccess.Should().BeTrue();
        var detail = HistoryDetail.Deserialize(_store.History.Single().Detail);
        detail.Should().ContainKey("Description").WhoseValue.Should().Be(new FieldChange("", "詳細を書く"));
    }

    [Fact]
    public async Task Update_EmptyTitle_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { Title = "  " });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.TitleRequired);
        _task.Title.Should().Be("元のタイトル");
    }

    [Fact]
    public async Task Update_UnknownProject_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(Unchanged() with { ProjectId = 999 });
        result.Error.Should().Be(Messages.ProjectNotFound);
    }

    [Fact]
    public async Task Update_UnknownTask_IsRejected()
    {
        var result = await _service.UpdateTaskAsync(new TaskUpdate(999, "x", "", null, null));
        result.Error.Should().Be(Messages.TaskNotFound);
    }

    [Fact]
    public async Task SetTaskLabels_ReplacesLabels_AndRecordsNames()
    {
        var urgent = _store.SeedLabel("至急");
        var routine = _store.SeedLabel("定例");
        _task.Labels.Add(urgent);

        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { routine.Id });

        result.IsSuccess.Should().BeTrue();
        _task.Labels.Select(l => l.Name).Should().Equal("定例");
        var detail = HistoryDetail.Deserialize(_store.History.Single().Detail);
        detail["Labels"].Should().Be(new FieldChange("至急", "定例"));
    }

    [Fact]
    public async Task SetTaskLabels_SameSet_WritesNoHistory()
    {
        var urgent = _store.SeedLabel("至急");
        _task.Labels.Add(urgent);

        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { urgent.Id, urgent.Id });

        result.IsSuccess.Should().BeTrue();
        _store.History.Should().BeEmpty();
    }

    [Fact]
    public async Task SetTaskLabels_UnknownLabel_IsRejected()
    {
        var result = await _service.SetTaskLabelsAsync(_task.Id, new[] { 999 });
        result.Error.Should().Be(Messages.LabelNotFound);
    }

    [Fact]
    public async Task SetTaskLabels_RecordsNamesInDisplayOrder()
    {
        var a = _store.SeedLabel("A");
        var b = _store.SeedLabel("B");
        b.Order = 0;
        a.Order = 1;

        await _service.SetTaskLabelsAsync(_task.Id, new[] { a.Id, b.Id });

        var detail = HistoryDetail.Deserialize(_store.History.Single().Detail);
        detail["Labels"].Should().Be(new FieldChange("", "B, A"));
    }
}
