using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>アーカイブの 1 行と、選んだときに右側へ出す詳細。読み取り専用なので、値は作るときに決め切る。</summary>
public sealed partial class ArchiveItemViewModel : ObservableObject
{
    /// <summary>曜日を「月」「火」で出すため。PC の言語設定に左右されないよう固定する。</summary>
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    public int Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string? ProjectName { get; }
    /// <summary>プロジェクト名の前に出す色丸のランプ段。色なしなら null。</summary>
    public string? ProjectColor { get; }
    public DateTime CompletedAt { get; }
    public DateOnly CompletedOn { get; }
    public string CompletedOnText { get; }
    public string CompletedAtText { get; }
    public string CreatedAtText { get; }
    public IReadOnlyList<LabelChip> Labels { get; }
    public bool HasLabels => Labels.Count > 0;

    [ObservableProperty] private bool _isSelected;

    /// <summary>アーカイブ対象（CompletedAt あり）だけを渡すこと。</summary>
    public ArchiveItemViewModel(TaskItem task, Project? project, TimeZoneInfo timeZone)
    {
        Id = task.Id;
        Title = task.Title;
        Description = task.Description;
        ProjectName = project?.Name;
        ProjectColor = project?.Color;
        CompletedAt = task.CompletedAt!.Value;
        CompletedOn = CompletedWeek.CompletedOn(CompletedAt, timeZone);
        CompletedOnText = Day(CompletedOn);
        CompletedAtText = HistoryFormatter.Timestamp(CompletedAt, timeZone);
        CreatedAtText = HistoryFormatter.Timestamp(task.CreatedAt, timeZone);
        Labels = task.Labels
            .InDisplayOrder()
            .Select(l => new LabelChip(l.Id, l.Name, l.Color))
            .ToList();
    }

    /// <summary>「9/12（土）」。週の見出しも同じ書式で出す。</summary>
    internal static string Day(DateOnly day) => day.ToString(Strings.ArchiveDayFormat, Japanese);
}
