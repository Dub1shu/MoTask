using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Ai;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemplateNotice))]
    private string _terminalCommandTemplate = "";
    [ObservableProperty] private string _planningInstruction = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>CLI が受け付ける値だけを選ばせる（仕様 §4.3）。</summary>
    public IReadOnlyList<string> PermissionModes => AiSettings.PermissionModes;

    // 生成されるプロパティ PlanningInstruction と同名なので、型は名前空間から引く。
    private static string DefaultPlanningInstruction => Core.Planning.PlanningInstruction.DefaultTemplate;

    /// <summary>
    /// wt.exe 始まりのテンプレートへの注意（仕様 §5.2）。計画づくりでは既定の起動に落ちる。
    /// 無ければ null（画面は NullToVisibility で隠す）。
    /// </summary>
    public string? TemplateNotice => TerminalLauncher.IsWindowsTerminalTemplate(TerminalCommandTemplate)
        ? Strings.PlanTemplateFallsBackToDefault
        : null;

    public AiSettingsViewModel(IAiSettingsStore store)
    {
        _store = store;

        var s = store.Load();
        _defaultWorkingDirectory = s.DefaultWorkingDirectory;
        _claudeExecutablePath = s.ClaudeExecutablePath ?? "";
        _model = s.Model ?? "";
        _permissionMode = s.PermissionMode;
        _terminalCommandTemplate = s.TerminalCommandTemplate ?? "";
        // 既定の文面は隠さずに見せる。人はそれを読んでから書き換える。
        _planningInstruction = s.PlanningInstruction ?? DefaultPlanningInstruction;
    }

    /// <summary>既定の文面に戻す。他の欄と同じく、保存するまでは反映しない。</summary>
    [RelayCommand]
    private void ResetPlanningInstruction() => PlanningInstruction = DefaultPlanningInstruction;

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
            mode, NullIfBlank(TerminalCommandTemplate), CustomPlanningInstructionOrNull()));
        ErrorMessage = null;
        StatusMessage = Strings.SettingsSaved;
    }

    /// <summary>
    /// 既定と同じなら null で持つ。既定の文面が改まったとき、書き換えていない人にもそのまま届く。
    /// </summary>
    private string? CustomPlanningInstructionOrNull()
    {
        // TextBox の Enter は CRLF を入れる。改行コードの違いだけで「書き換えた」扱いにしない。
        var text = NullIfBlank(PlanningInstruction);
        return text?.ReplaceLineEndings() == DefaultPlanningInstruction.Trim().ReplaceLineEndings() ? null : text;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
