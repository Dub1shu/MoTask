using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// フックの実体は「stdin を 1 行にして追記する」だけ。壊れると盤面が黙って止まるので、
/// 組み立てたコマンドではなく実際の exe を起動して確かめる。
/// </summary>
public class HooksExeTests : IDisposable
{
    private static readonly string Exe =
        Path.Combine(AppContext.BaseDirectory, "hooks", "MoTask.Hooks.exe");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public HooksExeTests() => Directory.CreateDirectory(_dir);

    private static async Task RunAsync(string target, string stdin)
    {
        var info = new ProcessStartInfo(Exe, $"\"{target}\"")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Exe_IsCopiedNextToTheApp()
    {
        File.Exists(Exe).Should().BeTrue("MoTask.App のビルドが hooks\\MoTask.Hooks.exe を運ぶはず");
    }

    [Fact]
    public async Task Appends_OneUtf8Line_PerInvocation()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, """{"hook_event_name":"Stop","last_assistant_message":"できました"}""");
        await RunAsync(target, """{"hook_event_name":"SessionEnd","reason":"exit"}""");

        var lines = File.ReadAllLines(target, new UTF8Encoding(false));
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("できました");
        lines[1].Should().Contain("SessionEnd");
    }

    [Fact]
    public async Task Folds_MultilineStdin_IntoOneLine()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, "{\r\n  \"hook_event_name\": \"SessionStart\"\r\n}");

        File.ReadAllLines(target).Should().ContainSingle()
            .Which.Should().Be("{ \"hook_event_name\": \"SessionStart\" }");
    }

    [Fact]
    public async Task Concurrent_Invocations_DoNotInterleave()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            RunAsync(target, $$"""{"hook_event_name":"PostToolUse","tool_use_id":"{{i}}"}""")));

        var lines = File.ReadAllLines(target);
        lines.Should().HaveCount(8);
        lines.Should().OnlyContain(l => l.StartsWith("{\"hook_event_name\"") && l.EndsWith("}"));
    }

    [Fact]
    public async Task EmptyStdin_WritesNothing()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        await RunAsync(target, "   \r\n  ");

        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task PreservesMultipleSpaces_WhenNoLineBreaksArePresent()
    {
        var target = Path.Combine(_dir, "events.jsonl");

        // JSON 文字列内の連続空白（改行なし）は保持される
        await RunAsync(target, """{"test":"a  b  c"}""");

        File.ReadAllLines(target).Should().ContainSingle()
            .Which.Should().Be("""{"test":"a  b  c"}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
