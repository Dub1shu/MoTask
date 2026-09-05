using System.Text.Json;
using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>
/// 「今後も許可」の記憶に使うパターンの作り方と一致条件（仕様 §6 AiPermissionRule）。
/// Bash は先頭 2 トークン（git 全体は広すぎ、全文一致は引数が変わるたびに聞かれて役に立たない）。
/// Write / Edit は対象ファイルのディレクトリ。それ以外は null（ツール名だけで一致）。
/// </summary>
public static class PermissionPattern
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>ダイアログに出す主題。Bash はコマンド全文、Write / Edit はファイルパス。読めない入力は null。</summary>
    public static string? Subject(PermissionRequest request) => request.ToolName switch
    {
        "Bash" => ReadString(request.InputJson, "command"),
        "Write" or "Edit" => ReadString(request.InputJson, "file_path"),
        _ => null,
    };

    /// <summary>記憶するパターン。null は「そのツール全部」。</summary>
    public static string? ForRemembering(PermissionRequest request)
    {
        var subject = Subject(request);
        if (subject is null) return null;
        return request.ToolName switch
        {
            "Bash" => string.Join(' ', subject.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2)),
            "Write" or "Edit" => DirectoryOf(subject),
            _ => null,
        };
    }

    public static bool Matches(AiPermissionRule rule, PermissionRequest request)
    {
        if (!string.Equals(rule.ToolName, request.ToolName, StringComparison.Ordinal)) return false;
        if (rule.Pattern is null) return true;

        var subject = Subject(request);
        if (subject is null) return false;

        return request.ToolName switch
        {
            "Bash" => MatchesCommand(rule.Pattern, subject),
            "Write" or "Edit" => IsUnderDirectory(rule.Pattern, subject),
            _ => false,
        };
    }

    /// <summary>「git push」は「git push origin」に一致し、「git pushx」には一致しない。</summary>
    private static bool MatchesCommand(string pattern, string command)
    {
        var normalized = string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Equals(pattern, StringComparison.Ordinal)
               || normalized.StartsWith(pattern + " ", StringComparison.Ordinal);
    }

    private static bool IsUnderDirectory(string directory, string filePath)
    {
        string fullDir, fullFile;
        try
        {
            fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            fullFile = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return fullFile.StartsWith(fullDir + Path.DirectorySeparatorChar, PathComparison)
               || fullFile.StartsWith(fullDir + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static string? DirectoryOf(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.IsNullOrEmpty(dir) ? null : Path.TrimEndingDirectorySeparator(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
