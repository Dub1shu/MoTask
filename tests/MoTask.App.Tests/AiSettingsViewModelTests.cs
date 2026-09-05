using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class AiSettingsViewModelTests
{
    private readonly IAiSettingsStore _store = Substitute.For<IAiSettingsStore>();

    public AiSettingsViewModelTests()
    {
        _store.Load().Returns(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5", AiSettings.DefaultPermissionMode, null));
    }

    private AiSettingsViewModel Open() => new(_store);

    [Fact]
    public void Open_LoadsSettingsIntoFields()
    {
        var vm = Open();

        vm.DefaultWorkingDirectory.Should().Be(@"C:\work");
        vm.ClaudeExecutablePath.Should().Be(@"C:\tools\claude.exe");
        vm.Model.Should().Be("claude-sonnet-5");
        vm.PermissionMode.Should().Be(AiSettings.DefaultPermissionMode);
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Save_WritesTrimmedValues_AndBlanksBecomeNull()
    {
        var vm = Open();
        vm.DefaultWorkingDirectory = @"  D:\ai  ";
        vm.ClaudeExecutablePath = "   ";
        vm.Model = "";

        vm.SaveCommand.Execute(null);

        _store.Received(1).Save(new AiSettings(@"D:\ai", null, null, AiSettings.DefaultPermissionMode, null));
        vm.StatusMessage.Should().Be(Strings.SettingsSaved);
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Save_RejectsABlankWorkingDirectory()
    {
        var vm = Open();
        vm.DefaultWorkingDirectory = "  ";

        vm.SaveCommand.Execute(null);

        vm.ErrorMessage.Should().Be(Strings.DefaultWorkingDirectoryRequired);
        _store.DidNotReceive().Save(Arg.Any<AiSettings>());
    }

    [Fact]
    public void Save_PersistsThePermissionModeAndTemplate()
    {
        var vm = Open();
        vm.PermissionMode = "plan";
        vm.TerminalCommandTemplate = "pwsh.exe -NoExit -Command {command}";

        vm.SaveCommand.Execute(null);

        _store.Received(1).Save(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            "plan", "pwsh.exe -NoExit -Command {command}"));
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Save_RejectsAPermissionModeTheCliDoesNotKnow()
    {
        var vm = Open();
        vm.PermissionMode = "すきなように";

        vm.SaveCommand.Execute(null);

        vm.ErrorMessage.Should().Be(Strings.PermissionModeInvalid);
        _store.DidNotReceive().Save(Arg.Any<AiSettings>());
    }

    [Fact]
    public void Save_TreatsABlankTemplateAsTheDefault()
    {
        var vm = Open();
        vm.TerminalCommandTemplate = "   ";

        vm.SaveCommand.Execute(null);

        _store.Received(1).Save(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null));
    }

    [Fact]
    public void PermissionModes_AreOfferedForTheDropDown()
    {
        var vm = Open();

        vm.PermissionModes.Should().Equal(AiSettings.PermissionModes);
    }
}
