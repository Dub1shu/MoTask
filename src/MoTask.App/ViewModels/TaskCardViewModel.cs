using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MoTask.App.Resources;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

/// <summary>カード1枚。Model は BoardService と共有する追跡済みエンティティで、変更後に Refresh で取り込む。</summary>
public sealed partial class TaskCardViewModel : ObservableObject
{
    public TaskItem Model { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private string _dueText = "";
    [ObservableProperty] private DueStatus _dueStatus;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private string? _aiBadgeText;
    [ObservableProperty] private bool _isAwaitingApproval;
    [ObservableProperty] private bool _hasAiBadge;

    public ObservableCollection<LabelChip> Labels { get; } = new();

    public TaskCardViewModel(TaskItem model)
    {
        Model = model;
    }

    public int Id => Model.Id;
    public int ColumnId => Model.ColumnId;
    public int Position => Model.Position;

    public void Refresh(Func<int?, string?> projectName, DateOnly today)
    {
        Title = Model.Title;
        ProjectName = projectName(Model.ProjectId);
        DueText = Model.DueDate is DateOnly d
            ? string.Format(CultureInfo.CurrentCulture, Strings.CardDueFormat, d.Month, d.Day)
            : "";
        DueStatus = DueStatuses.Of(Model, today);
        IsDeleted = Model.IsDeleted;
        Labels.Clear();
        foreach (var label in Model.Labels.OrderBy(l => l.Name, StringComparer.CurrentCulture))
        {
            Labels.Add(new LabelChip(label.Id, label.Name, label.Color));
        }
        OnPropertyChanged(nameof(ColumnId));
        OnPropertyChanged(nameof(Position));
    }

    /// <summary>
    /// AI ジョブの状態をバッジに写す（仕様 §10）。終了状態や null で消す。
    /// Refresh はモデルの項目しか見ないので、バッジはここでだけ変わる。
    /// </summary>
    public void SetAiState(AiJobSnapshot? job)
    {
        if (job is null || job.Status.IsTerminal() || job.Status == AiJobStatus.Pending)
        {
            AiBadgeText = null;
            IsAwaitingApproval = false;
            HasAiBadge = false;
            return;
        }

        IsAwaitingApproval = job.Status == AiJobStatus.AwaitingApproval;
        AiBadgeText = job.Status switch
        {
            AiJobStatus.AwaitingApproval => Strings.AiStatusAwaiting,
            AiJobStatus.Suspended => Strings.AiStatusSuspended,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.AiBadgeTurnsFormat,
                job.Kind == AiJobKind.Research ? Strings.AiStatusResearching : Strings.AiStatusExecuting, job.TurnCount),
        };
        HasAiBadge = true;
    }
}
