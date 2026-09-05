using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;

namespace MoTask.App.ViewModels;

/// <summary>
/// プロジェクトとラベルの管理ダイアログ。作成は詳細パネル側にあるので、ここは
/// 一覧・使用件数・アーカイブ／復元だけを扱う。改名も完全削除も v1 では持たない。
/// </summary>
public sealed partial class ManageClassificationsViewModel : ObservableObject
{
    private readonly BoardViewModel _board;

    public ObservableCollection<ClassificationRow> Projects { get; } = new();
    public ObservableCollection<ClassificationRow> Labels { get; } = new();

    /// <summary>テストが操作の完了を待つためのハンドル（詳細パネルと同じ作法）。</summary>
    public Task PendingChange { get; private set; } = Task.CompletedTask;

    public ManageClassificationsViewModel(BoardViewModel board)
    {
        _board = board;
        Refresh();
    }

    /// <summary>ボードの現在の一覧から行を組み直す。使用件数もここで数える。</summary>
    public void Refresh()
    {
        // 論理削除済みのタスクは「使っている」に数えない（列の件数表示と同じ考え方）
        var live = _board.Columns.SelectMany(c => c.AllCards).Select(c => c.Model).Where(t => !t.IsDeleted).ToList();

        Projects.Clear();
        foreach (var p in _board.Projects.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Projects.Add(new ClassificationRow(ClassificationKind.Project, p.Id, p.Name,
                live.Count(t => t.ProjectId == p.Id), p.Archived));
        }

        Labels.Clear();
        foreach (var l in _board.Labels.OrderBy(l => l.Name, StringComparer.CurrentCulture))
        {
            Labels.Add(new ClassificationRow(ClassificationKind.Label, l.Id, l.Name,
                live.Count(t => t.Labels.Any(x => x.Id == l.Id)), l.Archived));
        }
    }

    public async Task ArchiveProjectAsync(ClassificationRow row)
    {
        if (await _board.ArchiveProjectAsync(row.Id)) Refresh();
    }

    public async Task UnarchiveProjectAsync(ClassificationRow row)
    {
        if (await _board.UnarchiveProjectAsync(row.Id)) Refresh();
    }

    public async Task ArchiveLabelAsync(ClassificationRow row)
    {
        if (await _board.ArchiveLabelAsync(row.Id)) Refresh();
    }

    public async Task UnarchiveLabelAsync(ClassificationRow row)
    {
        if (await _board.UnarchiveLabelAsync(row.Id)) Refresh();
    }

    /// <summary>行の種別で振り分けるので、プロジェクトとラベルで同じ行テンプレートを使える。</summary>
    [RelayCommand]
    private void Toggle(ClassificationRow row) => PendingChange = (row.Kind, row.IsArchived) switch
    {
        (ClassificationKind.Project, false) => ArchiveProjectAsync(row),
        (ClassificationKind.Project, true) => UnarchiveProjectAsync(row),
        (ClassificationKind.Label, false) => ArchiveLabelAsync(row),
        _ => UnarchiveLabelAsync(row),
    };
}

public enum ClassificationKind
{
    Project,
    Label,
}

/// <summary>管理ダイアログの1行。プロジェクトとラベルで同じ形。</summary>
public sealed record ClassificationRow(ClassificationKind Kind, int Id, string Name, int UsageCount, bool IsArchived)
{
    public string UsageText => string.Format(CultureInfo.CurrentCulture, Strings.ManageUsageFormat, UsageCount);
}
