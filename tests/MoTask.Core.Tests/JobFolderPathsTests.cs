using System.IO;
using FluentAssertions;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Core.Tests;

public class JobFolderPathsTests
{
    [Fact]
    public void For_LaysOutTheFilesUnderTheRoot()
    {
        var paths = JobFolderPaths.For(@"C:\work\jobs\0042-請求書の突合");

        paths.Root.Should().Be(@"C:\work\jobs\0042-請求書の突合");
        paths.JobJson.Should().Be(Path.Combine(paths.Root, "job.json"));
        paths.InstructionMarkdown.Should().Be(Path.Combine(paths.Root, "instruction.md"));
        paths.HooksJson.Should().Be(Path.Combine(paths.Root, "hooks.json"));
        paths.EventsJsonl.Should().Be(Path.Combine(paths.Root, "events.jsonl"));
        paths.ArtifactsDirectory.Should().Be(Path.Combine(paths.Root, "artifacts"));
    }

    [Fact]
    public void FolderName_PadsTheJobIdToFourDigits()
    {
        JobFolderPaths.FolderName(42, "請求書の突合").Should().Be("0042-請求書の突合");
        JobFolderPaths.FolderName(12345, "大きい").Should().Be("12345-大きい");
    }

    [Theory]
    // Windows のファイル名に使えない文字は - に潰す
    [InlineData(@"a/b\c:d*e?f""g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    // 空白の連なりは 1 つの - にまとめる
    [InlineData("  請求書   の 突合  ", "請求書-の-突合")]
    // 連続する区切りは 1 つにまとめ、前後の - と . は落とす
    [InlineData("--..見積り..--", "見積り")]
    // 日本語はそのまま残す
    [InlineData("月次レポート", "月次レポート")]
    public void Slug_KeepsNamesUsableAsFolderNames(string title, string expected)
    {
        JobFolderPaths.Slug(title).Should().Be(expected);
    }

    [Fact]
    public void Slug_FallsBackWhenNothingIsLeft()
    {
        JobFolderPaths.Slug("").Should().Be("task");
        JobFolderPaths.Slug("///").Should().Be("task");
    }

    [Fact]
    public void Slug_IsCappedSoThePathStaysShort()
    {
        var slug = JobFolderPaths.Slug(new string('あ', 100));

        slug.Should().HaveLength(40);
    }
}
