using System.Text.Encodings.Web;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

/// <summary>
/// 承認ダイアログ（仕様 §9）。何を許すのか（Bash はコマンド全文、Write はパスと内容）と、
/// 「今後も」を選んだときに何が記憶されるのかを明示する。
/// </summary>
public sealed partial class PermissionDialogViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private bool _syncingScope;

    public string ToolLine { get; }
    public string Summary { get; }
    public string Detail { get; }
    public string TaskTitle { get; }
    public string WorkingDirectory { get; }
    public string RememberText { get; }
    public bool HasProject { get; }

    [ObservableProperty] private bool _rememberForProject;
    [ObservableProperty] private bool _rememberForAll;

    /// <summary>ボタンで決まる。× で閉じたときは null のまま。</summary>
    public HumanDecision? Decision { get; private set; }

    public event Action? Closed;

    public PermissionDialogViewModel(PermissionPromptContext context)
    {
        var request = context.Request;
        ToolLine = string.Format(Strings.PermissionToolFormat, request.ToolName);
        TaskTitle = context.TaskTitle;
        WorkingDirectory = context.Job.WorkingDirectory;
        HasProject = context.ProjectId is not null;
        _rememberForProject = HasProject;
        _rememberForAll = !HasProject;

        var subject = PermissionPattern.Subject(request);
        Summary = subject ?? request.ToolName;
        Detail = DetailOf(request);
        RememberText = string.Format(Strings.PermissionRememberFormat, RememberDescription(request.ToolName, context.RememberPattern));
    }

    private static string DetailOf(PermissionRequest request)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(request.InputJson);
        }
        catch (JsonException)
        {
            return request.InputJson;
        }
        using (doc)
        {
            var root = doc.RootElement;
            return request.ToolName switch
            {
                "Bash" => Read(root, "description") ?? "",
                "Write" => Read(root, "content") ?? "",
                "Edit" => string.Format(Strings.PermissionEditFormat, Read(root, "old_string") ?? "", Read(root, "new_string") ?? ""),
                _ => JsonSerializer.Serialize(root, Pretty),
            };
        }
    }

    private static string? Read(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string RememberDescription(string toolName, string? pattern) => (toolName, pattern) switch
    {
        (_, null) => string.Format(Strings.PermissionRememberToolOnly, toolName),
        ("Bash", _) => string.Format(Strings.PermissionRememberBashFormat, pattern),
        _ => string.Format(Strings.PermissionRememberDirFormat, pattern),
    };

    partial void OnRememberForProjectChanged(bool value) => SyncScope(fromProject: true, value);
    partial void OnRememberForAllChanged(bool value) => SyncScope(fromProject: false, value);

    /// <summary>ラジオボタン 2 つを排他にする。</summary>
    private void SyncScope(bool fromProject, bool value)
    {
        if (_syncingScope || !value) return;
        _syncingScope = true;
        try
        {
            if (fromProject) RememberForAll = false;
            else RememberForProject = false;
        }
        finally
        {
            _syncingScope = false;
        }
    }

    [RelayCommand]
    private void Allow() => Finish(RuleDecision.Allow, remember: false);

    [RelayCommand]
    private void Deny() => Finish(RuleDecision.Deny, remember: false);

    [RelayCommand]
    private void AllowAlways() => Finish(RuleDecision.Allow, remember: true);

    [RelayCommand]
    private void DenyAlways() => Finish(RuleDecision.Deny, remember: true);

    private void Finish(RuleDecision decision, bool remember)
    {
        var scope = remember && HasProject && RememberForProject ? RuleScope.Project : RuleScope.Global;
        Decision = new HumanDecision(decision, remember, scope);
        Closed?.Invoke();
    }
}
