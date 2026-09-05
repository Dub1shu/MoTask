using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.ViewModels;

public sealed record PermissionRuleRow(int Id, string Text);

/// <summary>仕様 §10「設定」。設定値は settings.json、許可ルールは DB（IAiJobService 経由）。</summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private readonly IAiSettingsStore _store;
    private readonly IAiJobService _jobs;
    private readonly IReadOnlyList<Project> _projects;

    [ObservableProperty] private string _defaultWorkingDirectory = "";
    [ObservableProperty] private string _maxConcurrentText = "";
    [ObservableProperty] private string _claudeExecutablePath = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _maxTurnsText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<PermissionRuleRow> Rules { get; } = new();

    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public AiSettingsViewModel(IAiSettingsStore store, IAiJobService jobs, IReadOnlyList<Project> projects)
    {
        _store = store;
        _jobs = jobs;
        _projects = projects;

        var s = store.Load();
        _defaultWorkingDirectory = s.DefaultWorkingDirectory;
        _maxConcurrentText = s.MaxConcurrentJobs.ToString(CultureInfo.CurrentCulture);
        _claudeExecutablePath = s.ClaudeExecutablePath ?? "";
        _model = s.Model ?? "";
        _maxTurnsText = s.MaxTurns.ToString(CultureInfo.CurrentCulture);
        PendingLoad = LoadRulesAsync();
    }

    public async Task LoadRulesAsync()
    {
        var rules = await _jobs.GetPermissionRulesAsync();
        Rules.Clear();
        foreach (var r in rules) Rules.Add(new PermissionRuleRow(r.Id, Describe(r)));
    }

    private string Describe(AiPermissionRule rule)
    {
        var decision = rule.Decision == RuleDecision.Allow ? Strings.SettingsRuleAllow : Strings.SettingsRuleDeny;
        var target = rule.Pattern is null
            ? $"{rule.ToolName}（{Strings.SettingsRuleAllTool}）"
            : $"{rule.ToolName}「{rule.Pattern}」";
        var scope = rule.Scope == RuleScope.Global
            ? Strings.SettingsScopeGlobal
            : string.Format(Strings.SettingsScopeProjectFormat, _projects.FirstOrDefault(p => p.Id == rule.ProjectId)?.Name ?? "?");
        return string.Format(Strings.SettingsRuleFormat, decision, target, scope);
    }

    [RelayCommand]
    private void Save()
    {
        StatusMessage = null;
        var dir = DefaultWorkingDirectory.Trim();
        if (dir.Length == 0)
        {
            ErrorMessage = Strings.DefaultWorkingDirectoryRequired;
            return;
        }
        if (!TryParsePositive(MaxConcurrentText, out var maxConcurrent))
        {
            ErrorMessage = Strings.MaxConcurrentMustBePositive;
            return;
        }
        if (!TryParsePositive(MaxTurnsText, out var maxTurns))
        {
            ErrorMessage = Strings.MaxTurnsMustBePositive;
            return;
        }

        _store.Save(new AiSettings(dir, maxConcurrent, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model), maxTurns));
        ErrorMessage = null;
        StatusMessage = Strings.SettingsSaved;
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(PermissionRuleRow row)
    {
        var result = await _jobs.DeletePermissionRuleAsync(row.Id);
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error;
            return;
        }
        ErrorMessage = null;
        await LoadRulesAsync();
    }

    private static bool TryParsePositive(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= 1;

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
