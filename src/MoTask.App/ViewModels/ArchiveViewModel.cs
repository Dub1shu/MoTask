using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>
/// アーカイブタブ。完了列のうち、今週より前に完了したものを週ごとに並べる。見るだけで、書き込みはしない。
/// ボードと同じ GetBoardAsync から拾うので、DB やサービスに専用の照会は持たない。
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly IBoardService _service;
    private readonly IClock _clock;
    /// <summary>重なった <see cref="LoadAsync"/> の世代。古い方は await から戻った時点で降りる（BoardViewModel と同じ手筋）。</summary>
    private int _reloadGeneration;

    public ObservableCollection<ArchiveWeekViewModel> Weeks { get; } = new();

    /// <summary>完了日の判定と日時の表示に使う。テストでは差し替える。</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    [ObservableProperty] private ArchiveItemViewModel? _selectedItem;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;

    public ArchiveViewModel(IBoardService service, IClock clock)
    {
        _service = service;
        _clock = clock;
    }

    /// <summary>
    /// タブを開くたびに呼ぶ。照会は例外を Result にしてくれないので、ここで包む
    /// （MainWindow 側は async void から呼ぶので、漏れるとプロセスが落ちる）。
    /// </summary>
    public async Task LoadAsync()
    {
        var generation = ++_reloadGeneration;
        Board board;
        IReadOnlyList<Project> projects;
        try
        {
            var result = await _service.GetBoardAsync();
            if (generation != _reloadGeneration) return;
            if (!result.IsSuccess)
            {
                ShowFailure(result.Error);
                return;
            }
            board = result.Value!;
            projects = await _service.GetProjectsAsync();
            if (generation != _reloadGeneration) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != _reloadGeneration) return;
            ShowFailure($"{Messages.SaveFailed}: {ex.Message}");
            return;
        }

        var today = _clock.Today;
        var items = board.Columns
            .Where(c => c.Role == ColumnRole.Done)
            .SelectMany(c => c.Tasks)
            .Where(t => !t.IsDeleted && CompletedWeek.IsArchived(t, today, TimeZone))
            .Select(t => new ArchiveItemViewModel(t, ProjectName(projects, t.ProjectId), TimeZone))
            .OrderByDescending(i => i.CompletedAt)
            .ThenByDescending(i => i.Id)
            .ToList();

        var selectedId = SelectedItem?.Id;
        Weeks.Clear();
        // GroupBy は最初に現れた順を保つので、新しい順に並べた items から作れば週も新しい順になる。
        foreach (var week in items.GroupBy(i => TaskFilter.WeekOf(i.CompletedOn).Start))
        {
            Weeks.Add(new ArchiveWeekViewModel(week.Key, week.ToList()));
        }
        ErrorMessage = null;
        IsEmpty = items.Count == 0;
        // 読み直した後も同じタスクがあれば選んだままにし、無ければ（ボードへ戻されたなど）詳細を閉じる。
        SelectedItem = selectedId is int id ? items.FirstOrDefault(i => i.Id == id) : null;
    }

    [RelayCommand]
    private void Select(ArchiveItemViewModel item) => SelectedItem = item;

    [RelayCommand]
    private void DismissBanner() => ErrorMessage = null;

    partial void OnSelectedItemChanged(ArchiveItemViewModel? oldValue, ArchiveItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>読めなかったときは一覧を空にする。「まだありません」は出さない（読めていないだけなので）。</summary>
    private void ShowFailure(string? error)
    {
        ErrorMessage = error;
        Weeks.Clear();
        SelectedItem = null;
        IsEmpty = false;
    }

    private static string? ProjectName(IReadOnlyList<Project> projects, int? projectId)
        => projectId is int id ? projects.FirstOrDefault(p => p.Id == id)?.Name : null;
}
