using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>
/// プロジェクト設定／ラベル設定のダイアログ。開くときの種別で片方だけを出す。一覧・使用件数のほか、追加・名前の変更・
/// 色の変更・プロジェクトの作業フォルダ・アーカイブ／復元を扱う。完全削除は持たない（過去のタスクの表示と履歴を壊さないため）。
/// </summary>
public sealed partial class ManageClassificationsViewModel : ObservableObject
{
    private readonly BoardViewModel _board;
    private readonly string? _defaultWorkingDirectory;

    public ObservableCollection<ClassificationRow> Projects { get; } = new();
    public ObservableCollection<ClassificationRow> Labels { get; } = new();

    // 新しいプロジェクト／ラベルの入力欄。詳細パネルの「＋」と同じく、使うときだけ開く。
    [ObservableProperty] private bool _isAddingProject;
    [ObservableProperty] private string _newProjectName = "";
    [ObservableProperty] private bool _isAddingLabel;
    [ObservableProperty] private string _newLabelName = "";

    /// <summary>ダイアログはモーダルで裏のバナーが見えないので、失敗はここに写して出す。次の操作を始めたら消す。</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>テストが操作の完了を待つためのハンドル（詳細パネルと同じ作法）。</summary>
    public Task PendingChange { get; private set; } = Task.CompletedTask;

    /// <param name="kind">どちらの設定として開くか。ダイアログの題名と、出す一覧を決める。</param>
    /// <param name="defaultWorkingDirectory">作業フォルダが未設定のプロジェクトに添えて出す、設定の既定フォルダ。</param>
    public ManageClassificationsViewModel(BoardViewModel board, ClassificationKind kind, string? defaultWorkingDirectory = null)
    {
        _board = board;
        Kind = kind;
        _defaultWorkingDirectory = defaultWorkingDirectory;
        Refresh();
    }

    public ClassificationKind Kind { get; }
    public bool IsProjects => Kind == ClassificationKind.Project;
    public bool IsLabels => Kind == ClassificationKind.Label;
    public string Title => IsProjects ? Strings.ManageProjects : Strings.ManageLabels;

    /// <summary>ボードの現在の一覧から行を組み直す。使用件数もここで数える。</summary>
    public void Refresh()
    {
        // 論理削除済みのタスクは「使っている」に数えない（列の件数表示と同じ考え方）
        var live = _board.Columns.SelectMany(c => c.AllCards).Select(c => c.Model).Where(t => !t.IsDeleted).ToList();

        Projects.Clear();
        foreach (var p in _board.Projects.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Projects.Add(new ClassificationRow(ClassificationKind.Project, p.Id, p.Name, p.Color,
                live.Count(t => t.ProjectId == p.Id), p.Archived, p.WorkingDirectory, _defaultWorkingDirectory));
        }

        Labels.Clear();
        foreach (var l in _board.Labels.OrderBy(l => l.Name, StringComparer.CurrentCulture))
        {
            Labels.Add(new ClassificationRow(ClassificationKind.Label, l.Id, l.Name, l.Color,
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
    private void Toggle(ClassificationRow row) => PendingChange = Run(() => (row.Kind, row.IsArchived) switch
    {
        (ClassificationKind.Project, false) => _board.ArchiveProjectAsync(row.Id),
        (ClassificationKind.Project, true) => _board.UnarchiveProjectAsync(row.Id),
        (ClassificationKind.Label, false) => _board.ArchiveLabelAsync(row.Id),
        _ => _board.UnarchiveLabelAsync(row.Id),
    });

    // ---------- 名前の変更 ----------

    [RelayCommand]
    private void BeginRename(ClassificationRow row)
    {
        ErrorMessage = null;
        row.EditName = row.Name;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename(ClassificationRow row) => row.IsEditing = false;

    /// <summary>空や今と同じ名前なら、保存せずに閉じるだけ（列の改名と同じ）。</summary>
    [RelayCommand]
    private void CommitRename(ClassificationRow row)
    {
        if (!row.IsEditing) return; // Enter で確定した後の LostFocus で二重に呼ばれる
        row.IsEditing = false;
        var name = row.EditName.Trim();
        if (name.Length == 0 || name == row.Name) return;

        PendingChange = Run(() => row.Kind == ClassificationKind.Project
            ? _board.RenameProjectAsync(row.Id, name)
            : _board.RenameLabelAsync(row.Id, name));
    }

    // ---------- 色 ----------

    [RelayCommand]
    private void SetColor(PaletteSwatch swatch)
    {
        ErrorMessage = null;
        if (swatch.IsSelected) return;
        PendingChange = Run(() => swatch.Row.Kind == ClassificationKind.Project
            ? _board.SetProjectColorAsync(swatch.Row.Id, swatch.Color)
            : _board.SetLabelColorAsync(swatch.Row.Id, swatch.Color!));
    }

    // ---------- 作業フォルダ ----------

    /// <summary>フォルダの選択ダイアログで選ばれたパスを保存する（ダイアログを開くのはビュー側）。今と同じなら何もしない。</summary>
    public void SetWorkingDirectory(ClassificationRow row, string path)
    {
        ErrorMessage = null;
        if (string.Equals(path, row.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) return;
        PendingChange = Run(() => _board.SetProjectWorkingDirectoryAsync(row.Id, path));
    }

    /// <summary>作業フォルダを消して、設定の既定フォルダを使う状態に戻す。</summary>
    [RelayCommand]
    private void ClearWorkingDirectory(ClassificationRow row)
        => PendingChange = Run(() => _board.SetProjectWorkingDirectoryAsync(row.Id, null));

    // ---------- 追加 ----------

    [RelayCommand]
    private void BeginAddProject()
    {
        ErrorMessage = null;
        NewProjectName = "";
        IsAddingProject = true;
    }

    [RelayCommand]
    private void CancelAddProject()
    {
        NewProjectName = "";
        IsAddingProject = false;
    }

    /// <summary>空の Enter は受け付けずに開いたまま、作れなかったときは入力を残す（詳細パネルと同じ）。</summary>
    [RelayCommand]
    private void CreateProject()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0) return;
        PendingChange = Run(async () =>
        {
            if (await _board.CreateProjectAsync(name) is null) return false;
            NewProjectName = "";
            IsAddingProject = false;
            return true;
        });
    }

    [RelayCommand]
    private void BeginAddLabel()
    {
        ErrorMessage = null;
        NewLabelName = "";
        IsAddingLabel = true;
    }

    [RelayCommand]
    private void CancelAddLabel()
    {
        NewLabelName = "";
        IsAddingLabel = false;
    }

    [RelayCommand]
    private void CreateLabel()
    {
        var name = NewLabelName.Trim();
        if (name.Length == 0) return;
        PendingChange = Run(async () =>
        {
            if (await _board.CreateLabelAsync(name) is null) return false;
            NewLabelName = "";
            IsAddingLabel = false;
            return true;
        });
    }

    // ---------- 共通 ----------

    /// <summary>操作を走らせ、成功なら行を組み直し、失敗ならボードのバナーの文言をダイアログに写す。</summary>
    private async Task Run(Func<Task<bool>> action)
    {
        ErrorMessage = null;
        if (await action()) Refresh();
        else ErrorMessage = _board.BannerMessage;
    }
}

public enum ClassificationKind
{
    Project,
    Label,
}

/// <summary>管理ダイアログの1行。プロジェクトとラベルで同じ形（「色なし」の見本はプロジェクトだけ）。</summary>
public sealed partial class ClassificationRow : ObservableObject
{
    public ClassificationRow(ClassificationKind kind, int id, string name, string? color, int usageCount, bool isArchived,
        string? workingDirectory = null, string? defaultWorkingDirectory = null)
    {
        Kind = kind;
        Id = id;
        Name = name;
        Color = color;
        UsageCount = usageCount;
        IsArchived = isArchived;
        WorkingDirectory = workingDirectory;
        _defaultWorkingDirectory = defaultWorkingDirectory;
        Swatches = LabelPalette.Colors.Select(c => new PaletteSwatch(this, c,
            string.Equals(c, color, StringComparison.OrdinalIgnoreCase))).ToList();
        // ラベルは必ず色を持つ。プロジェクトは既定が色なしなので、そこへ戻る見本を置く
        NoColorSwatch = kind == ClassificationKind.Project ? new PaletteSwatch(this, null, color is null) : null;
    }

    public ClassificationKind Kind { get; }
    public int Id { get; }
    public string Name { get; }
    /// <summary>ランプ段の名前。色なしのプロジェクトは null。</summary>
    public string? Color { get; }
    public int UsageCount { get; }
    public bool IsArchived { get; }
    public IReadOnlyList<PaletteSwatch> Swatches { get; }
    public PaletteSwatch? NoColorSwatch { get; }

    public bool IsProject => Kind == ClassificationKind.Project;
    /// <summary>AI ジョブを動かすフォルダ。null なら設定の既定フォルダを使う。プロジェクトだけが持つ。</summary>
    public string? WorkingDirectory { get; }
    public bool HasWorkingDirectory => WorkingDirectory is not null;
    private readonly string? _defaultWorkingDirectory;

    public string WorkingDirectoryText => WorkingDirectory
        ?? string.Format(CultureInfo.CurrentCulture, Strings.ManageDefaultWorkingDirectoryFormat, _defaultWorkingDirectory);

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";

    public string UsageText => string.Format(CultureInfo.CurrentCulture, Strings.ManageUsageFormat, UsageCount);
}

/// <summary>色のポップアップの見本 1 つ。押されたらどの行の色を変えるかを自分で持つ。Color が null なら「色なし」。</summary>
public sealed record PaletteSwatch(ClassificationRow Row, string? Color, bool IsSelected);
