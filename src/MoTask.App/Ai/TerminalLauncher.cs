using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 端末で claude を対話起動して手放す（仕様 §7）。プロセスは所有しないので、
/// 起動したハンドルはその場で捨てる。
/// </summary>
public sealed class TerminalLauncher : ISessionLauncher
{
    /// <summary>wt.exe があるときの既定。cwd は wt に渡す（新しいタブがそこで開く）。</summary>
    internal const string WindowsTerminalTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}";

    /// <summary>wt.exe が無い環境の逃げ道。cwd は ProcessStartInfo 側で渡す。</summary>
    internal const string FallbackTemplate = "cmd.exe /k {command}";

    private readonly IAiSettingsStore _settings;
    private readonly string? _pathVariable;
    private readonly Func<bool> _hasWindowsTerminal;

    public TerminalLauncher(IAiSettingsStore settings)
        : this(settings, null, () => FindWindowsTerminal() is not null)
    {
    }

    internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable, Func<bool> hasWindowsTerminal)
    {
        _settings = settings;
        _pathVariable = pathVariable;
        _hasWindowsTerminal = hasWindowsTerminal;
    }

    public Result CheckAvailable()
        => ClaudeLocator.Find(_settings.Load().ClaudeExecutablePath, _pathVariable) is null
            ? Result.Fail(Messages.ClaudeNotFound)
            : Result.Ok();

    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        var settings = _settings.Load();
        var claude = ClaudeLocator.Find(settings.ClaudeExecutablePath, _pathVariable);
        if (claude is null) return Result.Fail<TerminalCommand>(Messages.ClaudeNotFound);

        var paths = JobFolderPaths.For(request.JobFolder);
        var parts = new List<string> { claude, "--settings", paths.HooksJson };

        // 再開は --session-id ではなく --resume。同じ ID を両方に渡さない。
        parts.Add(request.Resume ? "--resume" : "--session-id");
        parts.Add(request.SessionId.ToString("D"));

        parts.Add("--permission-mode");
        parts.Add(settings.PermissionMode);
        // cwd はプロジェクト。ジョブフォルダはここで読み書きを許す（仕様 §6）。
        parts.Add("--add-dir");
        parts.Add(request.JobFolder);
        if (settings.Model is { Length: > 0 } model)
        {
            parts.Add("--model");
            parts.Add(model.Trim());
        }
        // 指示文そのものは渡さない。長文の引用符・改行をコマンドラインに持ち込まないため（仕様 §7）。
        parts.Add(string.Format(Messages.TerminalStartPromptFormat, paths.InstructionMarkdown, paths.ArtifactsDirectory));

        var inner = string.Join(" ", parts.Select(CommandLine.Quote));
        var template = settings.TerminalCommandTemplate is { Length: > 0 } configured
            ? configured
            : _hasWindowsTerminal() ? WindowsTerminalTemplate : FallbackTemplate;
        // テンプレート側で {cwd} はすでに "..." に囲まれている（既定テンプレートも利用者定義も同じ形）。
        // ドライブ直下（D:\ など）のように末尾が \ で終わる cwd をそのまま埋めると、
        // テンプレートの閉じ " の直前が奇数個の \ になり、CommandLineToArgvW がその " を
        // エスケープされた文字と解釈してしまい、以降が丸ごと 1 引数に飲み込まれる。
        // CommandLine.Quote と同じ規則で末尾の \ を 2 倍にしてから、Quote が付ける外側の "
        // だけを剥がして埋め込む（テンプレートの " と二重に囲まないため）。
        var quotedCwd = CommandLine.Quote(request.WorkingDirectory);
        var cwdForTemplate = quotedCwd.Substring(1, quotedCwd.Length - 2);
        var line = template.Replace("{cwd}", cwdForTemplate).Replace("{command}", inner);

        var (fileName, arguments) = CommandLine.SplitFirstToken(line);
        return Result.Ok(new TerminalCommand(fileName, arguments, request.WorkingDirectory));
    }

    public Result Launch(TerminalCommand command)
    {
        try
        {
            // UseShellExecute = true で自前のウィンドウを持たせる。返るハンドルは使わないので閉じる。
            using var started = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = command.WorkingDirectory,
            });
            return Result.Ok();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 何で失敗したかより「何を実行しようとしたか」が要る（仕様 §12）
            return Result.Fail(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
        }
    }

    private static string? FindWindowsTerminal()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH に不正な文字が混ざっていても探索を続ける
            }
        }
        return null;
    }
}
