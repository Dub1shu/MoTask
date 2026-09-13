using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 端末で claude を起こす（ターミナル AI 仕様 §7）。AI 遂行は起こして手放し、朝の実行は
/// Process ハンドルごと所有する（MCP 受け渡し仕様 §5）。Process を触るのはこのクラスだけ。
/// </summary>
public sealed class TerminalLauncher : ISessionLauncher
{
    /// <summary>AI 遂行の既定。claude が終わってもシェルを残す（続けて打てる）。</summary>
    internal const string DefaultTemplate = "cmd.exe /k {command}";

    /// <summary>朝の実行の既定。claude が終われば窓も畳む（仕様 §5.2）。</summary>
    internal const string MorningTemplate = "cmd.exe /c {command}";

    private readonly IAiSettingsStore _settings;
    private readonly string? _pathVariable;

    public TerminalLauncher(IAiSettingsStore settings)
        : this(settings, null)
    {
    }

    internal TerminalLauncher(IAiSettingsStore settings, string? pathVariable)
    {
        _settings = settings;
        _pathVariable = pathVariable;
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
        // オプションの終わりをここで閉じる。--add-dir は可変長（<directories...>）なので、
        // -- を挟まずにプロンプトを続けると、プロンプトが 2 つ目の許可ディレクトリとして
        // 飲み込まれ、claude はプロンプト無しの対話セッションとして起動してしまう
        // （端末は開くが何も始まらない。Claude Code 2.1.261 で確認）。
        parts.Add("--");
        // 指示文そのものは渡さない。長文の引用符・改行をコマンドラインに持ち込まないため（仕様 §7）。
        // 出力先は呼び出し元が JobFolderRequest.OutputDirectoryName に合わせて指定する
        // （AI 遂行は artifacts/、朝の実行は result/）。ここを artifacts 固定のままにすると、
        // 朝の実行の起動プロンプトが instruction.md の指示（result/ に書く）と矛盾してしまう。
        var outputDirectory = Path.Combine(paths.Root, request.OutputDirectoryName);
        parts.Add(string.Format(Messages.TerminalStartPromptFormat, paths.InstructionMarkdown, outputDirectory));

        var inner = string.Join(" ", parts.Select(CommandLine.Quote));
        var template = settings.TerminalCommandTemplate is { Length: > 0 } configured
            ? configured
            : request.CloseOnExit ? MorningTemplate : DefaultTemplate;
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
}
