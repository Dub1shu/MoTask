using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Resources;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class AiSettingsViewModelTests
{
    private readonly FakeAiSettingsStore _store = new();

    public AiSettingsViewModelTests()
    {
        _store.Settings = new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5", AiSettings.DefaultPermissionMode, null);
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

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"D:\ai", null, null, AiSettings.DefaultPermissionMode, null));
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
        _store.SaveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Save_PersistsThePermissionModeAndTemplate()
    {
        var vm = Open();
        vm.PermissionMode = "plan";
        vm.TerminalCommandTemplate = "pwsh.exe -NoExit -Command {command}";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
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
        _store.SaveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Save_TreatsABlankTemplateAsTheDefault()
    {
        var vm = Open();
        vm.TerminalCommandTemplate = "   ";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null));
    }

    [Fact]
    public void PermissionModes_AreOfferedForTheDropDown()
    {
        var vm = Open();

        vm.PermissionModes.Should().Equal(AiSettings.PermissionModes);
    }

    [Fact]
    public void Save_KeepsTheMorningInstruction()
    {
        var vm = Open();
        vm.MorningInstruction = "  私の方針  ";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, "私の方針"));
    }

    [Fact]
    public void Save_ClearsTheMorningInstruction_WhenTheBoxIsEmptied()
    {
        _store.Settings = new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, "前の方針");
        var vm = Open();
        vm.MorningInstruction = "";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, null));
    }

    [Fact]
    public void TemplateNotice_IsNullForAnOrdinaryTemplate()
    {
        var vm = new AiSettingsViewModel(new StubSettingsStore(AiSettings.Default()));

        vm.TemplateNotice.Should().BeNull();

        vm.TerminalCommandTemplate = "powershell.exe -NoExit -Command {command}";
        vm.TemplateNotice.Should().BeNull();
    }

    /// <summary>朝の実行では使えないテンプレートなので、保存前から画面で知らせる（仕様 §5.2）。</summary>
    [Fact]
    public void TemplateNotice_AppearsWhileTypingAWindowsTerminalTemplate()
    {
        var vm = new AiSettingsViewModel(new StubSettingsStore(AiSettings.Default()));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TerminalCommandTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}";

        vm.TemplateNotice.Should().Be(Strings.MorningTemplateFallsBackToDefault);
        raised.Should().Contain(nameof(AiSettingsViewModel.TemplateNotice));
    }

    private sealed class StubSettingsStore : IAiSettingsStore
    {
        private AiSettings _settings;
        public StubSettingsStore(AiSettings settings) => _settings = settings;
        public AiSettings Load() => _settings;
        public void Save(AiSettings settings) => _settings = settings;
    }
}
