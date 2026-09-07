using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Ai;

namespace MoTask.App.ViewModels;

/// <summary>仕様 §10「設定」。設定値は settings.json だけ（承認は利用者の Claude Code 設定に従う）。</summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private readonly IAiSettingsStore _store;

    [ObservableProperty] private string _defaultWorkingDirectory = "";
    [ObservableProperty] private string _claudeExecutablePath = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _permissionMode = "";
    [ObservableProperty] private string _terminalCommandTemplate = "";
    [ObservableProperty] private string _morningInstruction = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>CLI が受け付ける値だけを選ばせる（仕様 §4.3）。</summary>
    public IReadOnlyList<string> PermissionModes => AiSettings.PermissionModes;

    public AiSettingsViewModel(IAiSettingsStore store)
    {
        _store = store;

        var s = store.Load();
        _defaultWorkingDirectory = s.DefaultWorkingDirectory;
        _claudeExecutablePath = s.ClaudeExecutablePath ?? "";
        _model = s.Model ?? "";
        _permissionMode = s.PermissionMode;
        _terminalCommandTemplate = s.TerminalCommandTemplate ?? "";
        _morningInstruction = s.MorningInstruction ?? "";
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
        var mode = PermissionMode.Trim();
        if (!AiSettings.PermissionModes.Contains(mode))
        {
            ErrorMessage = Strings.PermissionModeInvalid;
            return;
        }

        _store.Save(new AiSettings(dir, NullIfBlank(ClaudeExecutablePath), NullIfBlank(Model),
            mode, NullIfBlank(TerminalCommandTemplate), NullIfBlank(MorningInstruction)));
        ErrorMessage = null;
        StatusMessage = Strings.SettingsSaved;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
