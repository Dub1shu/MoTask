using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// 端末で claude を起こす（ターミナル AI 仕様 §7）。AI 遂行は起こして手放し、朝の実行は
/// Process ハンドルごと所有する（MCP 受け渡し仕様 §5）。Process を触るのはこのクラスだけ。
/// </summary>
public sealed class TerminalLauncher : ISessionLauncher, IDisposable
{
    // 既定テンプレートが {command} を丸ごと "..." で囲み、さらに /s を付けているのは cmd の
    // 引用符規則のため（cmd /? の /c・/k の項）。cmd は /c・/k の後ろの文字列に引用符が
    // 3 つ以上あると、先頭の " と最後の " を剥がして残りを解析し直す。{command} は argv を
    // 1 個ずつ引用した文字列なので引用符が十数個あり、囲まずに渡すと実行ファイルのパスの
    // 引用が壊れて、cmd 自身が「ファイル名、ディレクトリ名、またはボリューム ラベルの構文が
    // 間違っています。」で落ちる。claude は起動せず events.jsonl すら生まれない（実測）。
    //   * 外側の " 1 組 …… 剥がされるのはこの 1 組だけになり、中身が無傷で残る
    //   * /s ……………… 「引用符を数える」条件分岐をやめて必ず外側 1 組を剥がす。引用符の数が
    //                   指示文次第で変わっても挙動が揺れない
    // 以前の既定は wt.exe を先頭に置いていた。wt が自分の引数を食って内側を組み直すので
    // この規則に当たらず、cmd 直叩きに変えた時点で初めて表に出た。外し方に見えても外さないこと。
    // なお利用者定義テンプレート（powershell.exe -Command {command} など）は cmd の解析を
    // 通らないので、囲いを足すのはこの 2 つの既定だけにする。

    /// <summary>AI 遂行の既定。claude が終わってもシェルを残す（続けて打てる）。</summary>
    internal const string DefaultTemplate = "cmd.exe /s /k \"{command}\"";

    /// <summary>朝の実行の既定。claude が終われば窓も畳む（仕様 §5.2）。</summary>
    internal const string PlanningTemplate = "cmd.exe /s /c \"{command}\"";

    /// <summary>
    /// 起動テンプレートの先頭トークンのファイル名が wt.exe か（フルパスも同じ扱い・大文字小文字は無視）。
    /// 朝の実行はこのテンプレートを使えないので既定に落とし、AI 設定画面に注意を出す（仕様 §5.2）。
    /// </summary>
    internal static bool IsWindowsTerminalTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        var (fileName, _) = CommandLine.SplitFirstToken(template);
        return fileName.Length > 0
               && string.Equals(Path.GetFileName(fileName), "wt.exe", StringComparison.OrdinalIgnoreCase);
    }

    private readonly IAiSettingsStore _settings;
    private readonly string? _pathVariable;

    /// <summary>
    /// 所有しているプロセス。ハンドルを開いたままにするのが要点で、Windows は開いている
    /// ハンドルのある pid を再利用しないため、「死んだ後に同じ pid の別プロセスを殺す」
    /// 事故が起きない（仕様 §5.3）。
    /// </summary>
    private readonly ConcurrentDictionary<int, Process> _owned = new();

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
        if (request.McpConfigPath is { Length: > 0 } mcpConfig)
        {
            // 利用者の手動 MCP 登録に依存しない（仕様 §5.4）。--strict-mcp-config は渡さないので、
            // 利用者のコネクタ（Gmail・カレンダーなど）はそのまま生きる。
            parts.Add("--mcp-config");
            parts.Add(mcpConfig);
        }
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
        // 成果物の出し先は AI 遂行だけが伝える。朝の実行は MCP で渡すので、伝えるものが無い（仕様 §5.4）。
        parts.Add(request.OutputDirectoryName is { Length: > 0 } output
            ? string.Format(Messages.TerminalStartPromptFormat,
                paths.InstructionMarkdown, Path.Combine(paths.Root, output))
            : string.Format(Messages.PlanningStartPromptFormat, paths.InstructionMarkdown));

        var inner = string.Join(" ", parts.Select(CommandLine.Quote));
        // 利用者定義のテンプレートは AI 遂行でも朝の実行でも効く。ただし朝の実行で wt を挟むと
        // 掴めるのが起動役の pid になり、完了時に閉じられない。そこだけ既定に落とす（仕様 §5.2）。
        var configured = settings.TerminalCommandTemplate is { Length: > 0 } text
                         && !(request.CloseOnExit && IsWindowsTerminalTemplate(text))
            ? text
            : null;
        var template = configured ?? (request.CloseOnExit ? PlanningTemplate : DefaultTemplate);
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

    public event EventHandler<int>? OwnedSessionExited;

    public Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command)
    {
        // try の外に置く。Start は成功したのに StartTime が投げた場合、catch から
        // このハンドルを閉じないと、端末は走っているのに Track もされず（CloseOwned の
        // 届かないところへ消える）、開いたままのハンドルが pid を永久に予約してしまう。
        Process? started = null;
        try
        {
            // UseShellExecute = true で自前のウィンドウを持たせる（Launch と同じ）。ハンドルは捨てずに持つ。
            started = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = command.WorkingDirectory,
            });
            if (started is null)
            {
                return Result.Fail<OwnedSession>(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
            }
            var session = new OwnedSession(started.Id, started.StartTime.ToUniversalTime());
            Track(ownerId, started);
            return Result.Ok(session);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 掴み損ねたプロセスのハンドルは閉じる（端末そのものは殺さない。§3 と同じ規律）。
            started?.Dispose();
            // 何で失敗したかより「何を実行しようとしたか」が要る（ターミナル AI 仕様 §12）
            return Result.Fail<OwnedSession>(string.Format(Messages.TerminalLaunchFailedFormat, command.Display));
        }
    }

    public void CloseOwned(int ownerId)
    {
        // 先に辞書から外す。Kill が起こす Exited は「こちらが意図した終了」なので、
        // OnExited の TryRemove が空振りして OwnedSessionExited には流れない。
        if (!_owned.TryRemove(ownerId, out var process)) return;
        try
        {
            // cmd / claude / MoTask.Mcp.exe をまとめて落とす。終了コードに依存しないので確実に窓が消える。
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // すでに終了している。目的は窓が消えることなので、消えているならそれでよい（仕様 §5.3）。
        }
        finally
        {
            process.Dispose();
        }
    }

    public bool TryReattach(int ownerId, int processId, DateTime startedAt)
    {
        if (processId <= 0) return false;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            // pid は再利用される。開始時刻が一致しなければ無関係のプロセスなので掴まない（仕様 §7）。
            if (process.HasExited || process.StartTime.ToUniversalTime() != startedAt)
            {
                process.Dispose();
                return false;
            }
            Track(ownerId, process);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return false;
        }
    }

    private void Track(int ownerId, Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(ownerId, process);
        if (_owned.TryRemove(ownerId, out var previous)) previous.Dispose();
        _owned[ownerId] = process;
        // 登録し終える前に死んでいた場合、Exited は _owned に居ない ownerId を見て黙って降りている。
        // 取りこぼさないようにここで拾い直す。ただし呼び出しスレッド上で直接呼ぶと、LaunchOwned /
        // TryReattach がまだ Result を返していない呼び出し元のスタックの上で OwnedSessionExited が
        // 走ってしまう。スレッドプールに逃がすのは、この再入を断ち（呼び出し元は通知に待たされない）、
        // 通常の Exited 経路と同じ「別スレッドから届く」形に揃えるため。
        // ※ 順序は保証されない — 通知が戻り値より先に届くことはありうるので、
        // 呼び出し元は終了通知を受け取れる状態を作ってから起動すること（PlanningService はそうしている）。
        if (process.HasExited) ThreadPool.QueueUserWorkItem(_ => OnExited(ownerId, process));
    }

    private void OnExited(int ownerId, Process process)
    {
        // CloseOwned が先に外していたら、こちらが起こした終了なので黙って降りる。
        // 値でも照合するのは、同じ ownerId に別のプロセスが入っていたときに
        // 生きているほうを蹴落とさないため。
        if (!_owned.TryRemove(new KeyValuePair<int, Process>(ownerId, process))) return;
        OwnedSessionExited?.Invoke(this, ownerId);
        process.Dispose();
    }

    /// <summary>
    /// MoTask を閉じたときに来る。ハンドルを解放するだけで端末は殺さない（仕様 §3・§5.3）。
    /// 実行の途中でアプリを閉じただけで仕事を潰さない。
    /// </summary>
    public void Dispose()
    {
        foreach (var ownerId in _owned.Keys)
        {
            if (_owned.TryRemove(ownerId, out var process)) process.Dispose();
        }
    }
}
