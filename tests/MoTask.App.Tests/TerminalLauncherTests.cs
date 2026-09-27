using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// 組み立てたコマンドを文字列として見る。claude も端末も起こさない（仕様 §13）。
/// 唯一の例外が BuildCommand_ProducesACommandLineCmdCanActuallyParse で、そこだけは
/// cmd.exe を窓無しで走らせる（理由はそのテストのコメント）。
/// </summary>
public class TerminalLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly string _claude;
    private readonly StubSettingsStore _store;
    private readonly SessionLaunchRequest _request;

    public TerminalLauncherTests()
    {
        Directory.CreateDirectory(_dir);
        _claude = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(_claude, "");
        _store = new StubSettingsStore(AiSettings.Default() with { ClaudeExecutablePath = _claude });
        _request = new SessionLaunchRequest(
            new Guid("6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77"),
            @"C:\work\jobs\0042-見積り",
            @"D:\repo\sample",
            Resume: false);
    }

    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }

    private TerminalLauncher Launcher() => new(_store, pathVariable: "");

    [Fact]
    public void CheckAvailable_FailsWhenClaudeIsMissing()
    {
        _store.Save(_store.Load() with { ClaudeExecutablePath = Path.Combine(_dir, "no-such.exe") });

        Launcher().CheckAvailable().Error.Should().Be(Messages.ClaudeNotFound);
    }

    [Fact]
    public void CheckAvailable_SucceedsWhenClaudeIsThere()
    {
        Launcher().CheckAvailable().IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// AI 遂行は cmd.exe /s /k。claude が終わってもシェルが残る（続けて打てる）。
    /// {command} 全体をもう 1 組の " で囲むのは cmd の引用符規則のため（TerminalLauncher の
    /// DefaultTemplate のコメント参照）。囲みが無いと cmd 自身の解析で落ちて何も起動しない。
    /// </summary>
    [Fact]
    public void BuildCommand_UsesCmdWithSlashK_ForAnAiJob()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/s /k \"");
        command.Arguments.Should().EndWith("\"", "{command} は閉じ \" で括り終える");
        // 既定テンプレートに {cwd} は現れない。cwd は ProcessStartInfo 側で渡す
        command.Arguments.Should().NotContain(@"-d ""D:\repo\sample""");
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
    }

    /// <summary>朝の実行は cmd.exe /s /c。claude が終われば窓も畳む（仕様 §5.2）。</summary>
    [Fact]
    public void BuildCommand_UsesCmdWithSlashC_ForAPlanningRun()
    {
        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/s /c \"");
        command.Arguments.Should().EndWith("\"", "{command} は閉じ \" で括り終える");
        command.Arguments.Should().Contain("--session-id");
    }

    /// <summary>
    /// ★ これがこの回帰を捕まえられる唯一のテスト。
    ///
    /// 「/k で始まる」式の文字列アサーションは前回すべて通ったまま、アプリは完全に壊れていた。
    /// cmd /? の規則では /c・/k の後ろの文字列に引用符が 3 つ以上あると、cmd は先頭の " と
    /// 最後の " を剥がして残りを解析し直す。BuildCommand の出力は argv を 1 個ずつ引用するので
    /// 引用符が十数個あり、剥がされた結果 実行ファイルのパスが壊れて cmd 自身が
    /// 「ファイル名、ディレクトリ名、またはボリューム ラベルの構文が間違っています。」で落ちる。
    /// claude は起動せず events.jsonl も生まれない。文字列では見えないので、実際に cmd に
    /// 食わせて「狙った実行ファイルが本当に起動したか」を見る。
    ///
    /// claude の代わりに、この場で書いた小さな .cmd を指す。中身は目印を 1 行出して
    /// exit 7 で（/k でも）cmd ごと終わるだけ。ロケール非依存の 2 つの証拠になる:
    ///   * 標準出力に目印がある = cmd が実行ファイルを解決して起動した
    ///   * 終了コードが 7 = 起動したのはまさにこの .cmd である
    /// 端末の窓は開かない（CreateNoWindow）。claude には触れない。1 秒もかからない。
    /// </summary>
    [Theory]
    [InlineData(false, false)] // AI 遂行（/s /k）
    [InlineData(true, false)]  // 朝の実行（/s /c）、mcp.json 無し
    // 朝の実行の実物の形。--mcp-config で引用符が 2 個増え、OutputDirectoryName は無い
    // （仕様 §5.4）。空白と日本語を含むパスも通す。
    [InlineData(true, true)]
    public void BuildCommand_ProducesACommandLineCmdCanActuallyParse(bool closeOnExit, bool planningShape)
    {
        var fakeClaude = Path.Combine(_dir, "fake-claude.cmd");
        File.WriteAllText(fakeClaude, "@echo off\r\necho " + LaunchMarker + "\r\nexit " + LaunchExitCode + "\r\n");
        _store.Save(_store.Load() with { ClaudeExecutablePath = fakeClaude });

        var request = _request with { CloseOnExit = closeOnExit };
        if (planningShape)
        {
            request = request with
            {
                OutputDirectoryName = null,
                McpConfigPath = @"C:\work\jobs\0007-2026 09 07\mcp 設定.json",
            };
        }
        var command = Launcher().BuildCommand(request).Value!;
        command.FileName.Should().Be("cmd.exe");

        var (exitCode, stdout, stderr) = RunCmd(command.Arguments);

        stdout.Should().Contain(
            LaunchMarker,
            "cmd が実行ファイルを解決して起動できたはず。stderr: {0}",
            stderr);
        exitCode.Should().Be(
            LaunchExitCode,
            "起動したのは組み立てた先頭トークンそのもののはず。stderr: {0}",
            stderr);
    }

    private const string LaunchMarker = "MOTASK_LAUNCHED_THE_TARGET";
    private const int LaunchExitCode = 7;

    /// <summary>cmd.exe を窓無し・全リダイレクトで走らせる。標準入力はすぐ閉じる（/k を待たせない）。</summary>
    private static (int ExitCode, string Stdout, string Stderr) RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        if (!process.WaitForExit(10_000))
        {
            // 保険。ここに来るのはテスト自身の取り回しが壊れたときだけ（本番の端末ではない）。
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("cmd.exe が終わらなかった: " + arguments);
        }
        Task.WaitAll(new Task[] { stdout, stderr }, 10_000);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void BuildCommand_PassesTheHookSettingsSessionIdPermissionModeAndJobFolder()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should().Contain("--settings\" \"C:\\work\\jobs\\0042-見積り\\hooks.json\"");
        command.Arguments.Should().Contain("--session-id\" \"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77\"");
        command.Arguments.Should().Contain("--permission-mode\" \"auto\"");
        command.Arguments.Should().Contain("--add-dir\" \"C:\\work\\jobs\\0042-見積り\"");
    }

    [Fact]
    public void BuildCommand_PointsThePromptAtTheInstructionAndTheArtifactsFolder()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should()
            .Contain(@"C:\work\jobs\0042-見積り\instruction.md")
            .And.Contain(@"C:\work\jobs\0042-見積り\artifacts");
    }

    /// <summary>
    /// 朝の実行は成果をファイルに出さないので、起動プロンプトに出し先を書かない（仕様 §5.4）。
    /// </summary>
    [Fact]
    public void BuildCommand_PointsThePromptOnlyAtTheInstruction_ForAPlanningRun()
    {
        var planning = _request with { OutputDirectoryName = null, CloseOnExit = true };

        var command = Launcher().BuildCommand(planning).Value!;

        command.Arguments.Should()
            .Contain(@"C:\work\jobs\0042-見積り\instruction.md")
            .And.NotContain(@"C:\work\jobs\0042-見積り\artifacts")
            .And.NotContain(@"C:\work\jobs\0042-見積り\result");
    }

    /// <summary>
    /// --add-dir は可変長（&lt;directories...&gt;）なので、その直後に置いたプロンプトは
    /// 2 つ目の許可ディレクトリとして飲み込まれ、claude はプロンプト無しで起動してしまう
    /// （Claude Code 2.1.261 で確認）。オプションの終わりを -- で閉じてから渡す。
    /// </summary>
    [Fact]
    public void BuildCommand_ClosesTheOptionsBeforeThePrompt()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should().Contain(@"""--"" ""C:\work\jobs\0042-見積り\instruction.md");
    }

    /// <summary>モデルを設定してもしなくても、プロンプトの直前は -- のままにする。</summary>
    [Fact]
    public void BuildCommand_ClosesTheOptionsBeforeThePromptWithAModelToo()
    {
        _store.Save(_store.Load() with { Model = "claude-opus-5" });

        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should().Contain(@"""--"" ""C:\work\jobs\0042-見積り\instruction.md");
    }

    [Fact]
    public void BuildCommand_NeverPassesTheArgumentsTheSpecForbids()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.Arguments.Should()
            .NotContain("--setting-sources").And.NotContain("--permission-prompt-tool")
            .And.NotContain("--tools").And.NotContain("--max-turns")
            .And.NotContain("--strict-mcp-config").And.NotContain("--mcp-config");
    }

    [Fact]
    public void BuildCommand_OmitsTheModelUnlessItIsConfigured()
    {
        Launcher().BuildCommand(_request).Value!.Arguments.Should().NotContain("--model");

        _store.Save(_store.Load() with { Model = "claude-opus-5" });
        Launcher().BuildCommand(_request).Value!.Arguments.Should().Contain("--model\" \"claude-opus-5\"");
    }

    [Fact]
    public void BuildCommand_ResumesWithTheSameSessionId()
    {
        var command = Launcher().BuildCommand(_request with { Resume = true }).Value!;

        command.Arguments.Should()
            .Contain("--resume\" \"6f2f2f1e-6c1e-4a6b-9d5c-2f0a5a1d3b77\"")
            .And.NotContain("--session-id");
    }

    [Fact]
    public void BuildCommand_HonoursTheConfiguredTemplate()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "pwsh.exe -NoExit -Command {command}" });

        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("pwsh.exe");
        command.Arguments.Should().StartWith("-NoExit -Command ");
        command.Arguments.Should().Contain("--session-id");
    }

    [Fact]
    public void BuildCommand_SubstitutesCwdInTheConfiguredTemplate()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "wt.exe -d \"{cwd}\" wsl {command}" });

        Launcher().BuildCommand(_request).Value!.Arguments.Should().StartWith("-d \"D:\\repo\\sample\" wsl ");
    }

    [Fact]
    public void BuildCommand_DoublesATrailingBackslashInCwdSoTheTemplateQuoteStillCloses()
    {
        // ドライブ直下（D:\）も正当な作業ディレクトリ。末尾の \ をそのまま埋めると
        // テンプレートの閉じ " が \" と解釈され、以降が丸ごと 1 引数に飲み込まれてしまう。
        // 既定テンプレートに {cwd} は無くなったので、置換の規則は利用者定義テンプレートで固定する。
        _store.Save(_store.Load() with { TerminalCommandTemplate = "pwsh.exe -d \"{cwd}\" -Command {command}" });

        var command = Launcher().BuildCommand(_request with { WorkingDirectory = @"D:\" }).Value!;

        command.Arguments.Should().StartWith("-d \"D:\\\\\" -Command ");
        command.Arguments.Should().Contain("--session-id");
    }

    /// <summary>
    /// wt を経由すると Process.Start が返すのは即座に終了する起動役の pid で、完了時に
    /// 端末を閉じられない。朝の実行のときだけ既定に落とす（仕様 §5.2）。
    /// </summary>
    [Theory]
    [InlineData("wt.exe -d \"{cwd}\" cmd /k {command}")]
    [InlineData("WT.EXE -d \"{cwd}\" cmd /k {command}")]
    [InlineData("\"C:\\Program Files\\WindowsApps\\wt.exe\" -d \"{cwd}\" cmd /k {command}")]
    public void BuildCommand_FallsBackToThePlanningDefault_WhenTheTemplateStartsWithWindowsTerminal(string template)
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = template });

        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/s /c \"");
        command.Arguments.Should().EndWith("\"");
    }

    /// <summary>AI 遂行では利用者のテンプレートをそのまま使う（閉じる必要が無い）。</summary>
    [Fact]
    public void BuildCommand_KeepsAWindowsTerminalTemplate_ForAnAiJob()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}" });

        Launcher().BuildCommand(_request).Value!.FileName.Should().Be("wt.exe");
    }

    /// <summary>先頭プロセスがウィンドウの持ち主なら、朝の実行でもそのまま使える（仕様 §5.2）。</summary>
    [Fact]
    public void BuildCommand_KeepsAPowerShellTemplate_ForAPlanningRun()
    {
        _store.Save(_store.Load() with { TerminalCommandTemplate = "powershell.exe -NoExit -Command {command}" });

        var command = Launcher().BuildCommand(_request with { CloseOnExit = true }).Value!;

        command.FileName.Should().Be("powershell.exe");
        command.Arguments.Should().StartWith("-NoExit -Command ");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("wt.exe -d \"{cwd}\" cmd /k {command}", true)]
    [InlineData("\"C:\\x\\wt.exe\" {command}", true)]
    [InlineData("wt {command}", false)]
    [InlineData("pwsh.exe -Command wt.exe {command}", false)]
    public void IsWindowsTerminalTemplate_LooksOnlyAtTheFirstTokensFileName(string? template, bool expected)
    {
        TerminalLauncher.IsWindowsTerminalTemplate(template).Should().Be(expected);
    }

    [Fact]
    public void BuildCommand_PassesTheMcpConfigWhenOneIsGiven()
    {
        var command = Launcher()
            .BuildCommand(_request with { McpConfigPath = @"C:\work\jobs\0042-見積り\mcp.json" }).Value!;

        command.Arguments.Should().Contain(@"--mcp-config"" ""C:\work\jobs\0042-見積り\mcp.json""");
        command.Arguments.Should().NotContain("--strict-mcp-config",
            "利用者のコネクタはそのまま生きる（仕様 §5.4）");
    }

    [Fact]
    public void BuildCommand_FailsWhenClaudeIsMissing()
    {
        _store.Save(_store.Load() with { ClaudeExecutablePath = Path.Combine(_dir, "no-such.exe") });

        var command = Launcher().BuildCommand(_request);

        command.IsSuccess.Should().BeFalse();
        command.Error.Should().Be(Messages.ClaudeNotFound);
    }

    [Fact]
    public void CloseOwned_IgnoresAnOwnerItNeverLaunched()
    {
        var launcher = Launcher();

        launcher.Invoking(l => l.CloseOwned(4242)).Should().NotThrow("知らない ownerId は黙って無視する");
    }

    [Fact]
    public void Dispose_DoesNotThrowWhenNothingIsOwned()
    {
        Launcher().Invoking(l => l.Dispose()).Should().NotThrow();
    }

    [Fact]
    public void TryReattach_FailsWhenThereIsNoSuchProcess()
    {
        Launcher().TryReattach(1, 0, DateTime.UtcNow).Should().BeFalse("pid が記録されていない");
        Launcher().TryReattach(1, int.MaxValue, DateTime.UtcNow).Should().BeFalse("そんな pid は居ない");
    }

    /// <summary>
    /// pid は再利用される。開始時刻が合わないなら無関係のプロセスなので掴んではいけない（仕様 §7）。
    /// テストプロセス自身は必ず生きているので、偽の開始時刻で拒否されることだけを確かめる。
    /// </summary>
    [Fact]
    public void TryReattach_FailsWhenTheStartTimeDoesNotMatch()
    {
        using var self = Process.GetCurrentProcess();

        Launcher().TryReattach(1, self.Id, self.StartTime.ToUniversalTime().AddSeconds(1)).Should().BeFalse();
    }

    /// <summary>掛け直しても殺さない。Dispose はハンドルを手放すだけである（仕様 §5.3）。</summary>
    [Fact]
    public void TryReattach_SucceedsWhenThePidAndStartTimeMatch()
    {
        using var self = Process.GetCurrentProcess();
        var launcher = Launcher();

        launcher.TryReattach(1, self.Id, self.StartTime.ToUniversalTime()).Should().BeTrue();

        launcher.Invoking(l => l.Dispose()).Should().NotThrow();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
