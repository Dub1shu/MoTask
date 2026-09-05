using System.IO;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class ClaudeLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public ClaudeLocatorTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void ConfiguredPath_IsUsedWhenItExists_AndRejectedWhenMissing()
    {
        var exe = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(exe, "");

        ClaudeLocator.Find(exe, pathVariable: "").Should().Be(exe);
        ClaudeLocator.Find(Path.Combine(_dir, "missing.exe"), pathVariable: "").Should().BeNull("設定されたパスが無いときは PATH へ逃げない");
    }

    [Fact]
    public void SearchesPathFolders_PreferringExe()
    {
        var other = Path.Combine(_dir, "other");
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "");
        File.WriteAllText(Path.Combine(bin, "claude.exe"), "");
        var path = string.Join(Path.PathSeparator, other, bin);

        ClaudeLocator.Find(null, path).Should().Be(Path.Combine(bin, "claude.exe"));
        ClaudeLocator.Find("   ", path).Should().Be(Path.Combine(bin, "claude.exe"), "空白だけの設定は未設定扱い");
    }

    [Fact]
    public void ReturnsNull_WhenNothingIsFound()
    {
        ClaudeLocator.Find(null, _dir).Should().BeNull();
        ClaudeLocator.Find(null, "").Should().BeNull();
        ClaudeLocator.Find(null, null).Should().Match(p => p == null || File.Exists(p), "実 PATH は環境次第");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
