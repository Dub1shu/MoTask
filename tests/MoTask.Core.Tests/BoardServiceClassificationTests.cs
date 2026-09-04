using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Model;
using MoTask.Core.Services;
using MoTask.Core.Tests.Fakes;
using Xunit;

namespace MoTask.Core.Tests;

public class BoardServiceClassificationTests
{
    private readonly InMemoryStore _store = new();
    private readonly BoardService _service;

    public BoardServiceClassificationTests()
    {
        _store.SeedColumn("完了", ColumnRole.Done);
        _service = new BoardService(_store, _store, _store, new FakeClock());
    }

    [Fact]
    public async Task CreateProject_TrimsName_AndAssignsId()
    {
        var result = await _service.CreateProjectAsync(" 顧客A対応 ");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().BePositive();
        result.Value.Name.Should().Be("顧客A対応");
        result.Value.Archived.Should().BeFalse();
        (await _service.GetProjectsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateProject_EmptyName_IsRejected()
    {
        (await _service.CreateProjectAsync("  ")).Error.Should().Be(Messages.ProjectNameRequired);
    }

    [Fact]
    public async Task ArchiveProject_SetsArchived()
    {
        var p = _store.SeedProject("合宿");
        (await _service.ArchiveProjectAsync(p.Id)).IsSuccess.Should().BeTrue();
        p.Archived.Should().BeTrue();
        (await _service.ArchiveProjectAsync(999)).Error.Should().Be(Messages.ProjectNotFound);
    }

    [Fact]
    public async Task CreateLabel_UsesGivenColor_OrDefault()
    {
        var a = await _service.CreateLabelAsync("至急", "accent-500");
        a.Value!.Color.Should().Be("accent-500");

        var b = await _service.CreateLabelAsync("定例", "");
        b.Value!.Color.Should().Be(Label.DefaultColor);

        // 名前の並び順は Japanese collation に依存するため、順序ではなく
        // 各ラベルの色を名前で引いて直接比較する（ルール4対応）。
        var labels = await _service.GetLabelsAsync();
        labels.Should().HaveCount(2);
        labels.Single(l => l.Name == "至急").Color.Should().Be("accent-500");
        labels.Single(l => l.Name == "定例").Color.Should().Be(Label.DefaultColor);
    }

    [Fact]
    public async Task CreateLabel_EmptyName_IsRejected()
    {
        (await _service.CreateLabelAsync(" ", "accent-300")).Error.Should().Be(Messages.LabelNameRequired);
    }
}
