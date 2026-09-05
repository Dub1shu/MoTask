using System.IO;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>実起動はしない（仕様 §13）。組み立てたコマンドを文字列として見る。</summary>
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

    private TerminalLauncher Launcher(bool hasWt = true)
        => new(_store, pathVariable: "", hasWindowsTerminal: () => hasWt);

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

    [Fact]
    public void BuildCommand_UsesWindowsTerminalWithTheProjectAsCwd()
    {
        var command = Launcher().BuildCommand(_request).Value!;

        command.FileName.Should().Be("wt.exe");
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
        command.Arguments.Should().StartWith("-d \"D:\\repo\\sample\" cmd /k ");
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
    public void BuildCommand_FallsBackToCmdWhenWindowsTerminalIsMissing()
    {
        var command = Launcher(hasWt: false).BuildCommand(_request).Value!;

        command.FileName.Should().Be("cmd.exe");
        command.Arguments.Should().StartWith("/k ");
        // wt が無いので cwd は ProcessStartInfo 側で渡す
        command.WorkingDirectory.Should().Be(@"D:\repo\sample");
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
        var command = Launcher().BuildCommand(_request with { WorkingDirectory = @"D:\" }).Value!;

        command.Arguments.Should().StartWith("-d \"D:\\\\\" cmd /k ");
        command.Arguments.Should().Contain("--session-id");
    }

    [Fact]
    public void BuildCommand_FailsWhenClaudeIsMissing()
    {
        _store.Save(_store.Load() with { ClaudeExecutablePath = Path.Combine(_dir, "no-such.exe") });

        var command = Launcher().BuildCommand(_request);

        command.IsSuccess.Should().BeFalse();
        command.Error.Should().Be(Messages.ClaudeNotFound);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
