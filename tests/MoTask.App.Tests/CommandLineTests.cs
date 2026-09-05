using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData(@"C:\Program Files\claude.exe", "\"C:\\Program Files\\claude.exe\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("末尾が\\", "\"末尾が\\\\\"")]
    [InlineData("引用符\"入り", "\"引用符\\\"入り\"")]
    public void Quote_WrapsAndEscapesForWindows(string value, string expected)
    {
        CommandLine.Quote(value).Should().Be(expected);
    }

    [Fact]
    public void SplitFirstToken_TakesTheExecutable()
    {
        var (file, args) = CommandLine.SplitFirstToken("wt.exe -d \"C:\\a b\" cmd /k x");

        file.Should().Be("wt.exe");
        args.Should().Be("-d \"C:\\a b\" cmd /k x");
    }

    [Fact]
    public void SplitFirstToken_HandlesAQuotedExecutable()
    {
        var (file, args) = CommandLine.SplitFirstToken("\"C:\\Program Files\\wt.exe\" -d x");

        file.Should().Be("C:\\Program Files\\wt.exe");
        args.Should().Be("-d x");
    }

    [Fact]
    public void SplitFirstToken_HandlesAnExecutableOnItsOwn()
    {
        CommandLine.SplitFirstToken("cmd.exe").Should().Be(("cmd.exe", ""));
    }
}
