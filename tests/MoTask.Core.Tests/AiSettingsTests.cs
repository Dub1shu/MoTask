using FluentAssertions;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Core.Tests;

public class AiSettingsTests
{
    [Fact]
    public void Default_UsesAutoPermissionModeAndNoTemplate()
    {
        var settings = AiSettings.Default();

        settings.PermissionMode.Should().Be("auto");
        settings.TerminalCommandTemplate.Should().BeNull();
    }

    [Fact]
    public void PermissionModes_AreTheSixValuesTheCliAccepts()
    {
        AiSettings.PermissionModes.Should().Equal(
            "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan");
    }

    [Fact]
    public void PermissionModes_ContainsTheDefault()
    {
        AiSettings.PermissionModes.Should().Contain(AiSettings.DefaultPermissionMode);
    }
}
