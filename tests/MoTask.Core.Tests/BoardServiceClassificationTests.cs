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

    [Fact]
    public async Task CreateLabel_IsNotArchived()
    {
        (await _service.CreateLabelAsync("至急", "accent-500")).Value!.Archived.Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveLabel_SetsArchived()
    {
        var l = _store.SeedLabel("至急");

        (await _service.ArchiveLabelAsync(l.Id)).IsSuccess.Should().BeTrue();

        l.Archived.Should().BeTrue();
        (await _service.ArchiveLabelAsync(999)).Error.Should().Be(Messages.LabelNotFound);
    }

    /// <summary>既にその状態なら保存しない（既存の ArchiveProject と同じ冪等性）。</summary>
    [Fact]
    public async Task ArchiveLabel_WhenAlreadyArchived_DoesNotSaveAgain()
    {
        var l = _store.SeedLabel("至急");
        await _service.ArchiveLabelAsync(l.Id);
        var saves = _store.SaveCount;

        (await _service.ArchiveLabelAsync(l.Id)).IsSuccess.Should().BeTrue();

        _store.SaveCount.Should().Be(saves);
    }

    [Fact]
    public async Task UnarchiveLabel_ClearsArchived()
    {
        var l = _store.SeedLabel("至急");
        await _service.ArchiveLabelAsync(l.Id);

        (await _service.UnarchiveLabelAsync(l.Id)).IsSuccess.Should().BeTrue();

        l.Archived.Should().BeFalse();
        (await _service.UnarchiveLabelAsync(999)).Error.Should().Be(Messages.LabelNotFound);
    }

    [Fact]
    public async Task UnarchiveLabel_WhenNotArchived_DoesNotSaveAgain()
    {
        var l = _store.SeedLabel("至急");
        var saves = _store.SaveCount;

        (await _service.UnarchiveLabelAsync(l.Id)).IsSuccess.Should().BeTrue();

        _store.SaveCount.Should().Be(saves);
    }

    [Fact]
    public async Task UnarchiveProject_ClearsArchived()
    {
        var p = _store.SeedProject("合宿");
        await _service.ArchiveProjectAsync(p.Id);

        (await _service.UnarchiveProjectAsync(p.Id)).IsSuccess.Should().BeTrue();

        p.Archived.Should().BeFalse();
        (await _service.UnarchiveProjectAsync(999)).Error.Should().Be(Messages.ProjectNotFound);
    }

    [Fact]
    public async Task UnarchiveProject_WhenNotArchived_DoesNotSaveAgain()
    {
        var p = _store.SeedProject("合宿");
        var saves = _store.SaveCount;

        (await _service.UnarchiveProjectAsync(p.Id)).IsSuccess.Should().BeTrue();

        _store.SaveCount.Should().Be(saves);
    }

    /// <summary>
    /// アーカイブしてもタスクとの紐付きは切らない。切ると過去のタスクの表示と
    /// 履歴の文言が壊れる（アーカイブを選んだ理由がこれ）。
    /// </summary>
    [Fact]
    public async Task ArchiveLabel_KeepsItAttachedToTasks()
    {
        var l = _store.SeedLabel("至急");
        var column = _store.SeedColumn("未着手", ColumnRole.Backlog);
        var task = (await _service.CreateTaskAsync(column.Id, "テスト")).Value!;
        await _service.SetTaskLabelsAsync(task.Id, new[] { l.Id });

        await _service.ArchiveLabelAsync(l.Id);

        task.Labels.Should().ContainSingle().Which.Id.Should().Be(l.Id);
        (await _service.GetLabelsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task SetProjectWorkingDirectory_TrimsAndStores_EmptyBecomesNull()
    {
        var project = _store.SeedProject("顧客A");

        (await _service.SetProjectWorkingDirectoryAsync(project.Id, @"  C:\work\a  ")).IsSuccess.Should().BeTrue();
        project.WorkingDirectory.Should().Be(@"C:\work\a");

        (await _service.SetProjectWorkingDirectoryAsync(project.Id, "   ")).IsSuccess.Should().BeTrue();
        project.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task SetProjectWorkingDirectory_UnknownProject_IsRejected()
    {
        (await _service.SetProjectWorkingDirectoryAsync(999, @"C:\x")).Error.Should().Be(Messages.ProjectNotFound);
    }
}
