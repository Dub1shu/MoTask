using FluentAssertions;
using MoTask.Mcp;
using Xunit;

namespace MoTask.Mcp.Tests;

/// <summary>
/// MoTask.App は MoTask.Mcp をアセンブリとして参照しないので、
/// src/MoTask.App/Ai/McpConfigJson.cs の AppExeOverrideVariable はこの定数を直接参照できず、
/// 独立した文字列リテラル "MOTASK_APP_EXE" として持っている。片方だけ綴りが変わると mcp.json が
/// 静かに効かなくなり、計画づくり中に MoTask を閉じたときだけ表に出る。この 2 つは対で綴りを固定する
/// (もう一方は tests/MoTask.App.Tests/McpConfigJsonTests.cs の
/// Build_PassesTheAppExeThroughTheOverrideEnvironmentVariable)。
/// </summary>
public class SystemAppHostTests
{
    [Fact]
    public void ExeOverrideVariable_IsSpelledMotaskAppExe()
        => SystemAppHost.ExeOverrideVariable.Should().Be("MOTASK_APP_EXE");
}
