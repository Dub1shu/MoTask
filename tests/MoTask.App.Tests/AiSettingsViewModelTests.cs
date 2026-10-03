using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.Tests.Fakes;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;
using MoTask.Core.Planning;
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
    public void Save_KeepsThePlanningInstruction()
    {
        var vm = Open();
        vm.PlanningInstruction = "  私の方針  ";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, "私の方針"));
    }

    [Fact]
    public void Save_ClearsThePlanningInstruction_WhenTheBoxIsEmptied()
    {
        _store.Settings = new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, "前の方針");
        var vm = Open();
        vm.PlanningInstruction = "";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(new AiSettings(@"C:\work", @"C:\tools\claude.exe", "claude-sonnet-5",
            AiSettings.DefaultPermissionMode, null, null));
    }

    /// <summary>既定の文面は隠さずに見せる。人はそれを読んでから書き換える。</summary>
    [Fact]
    public void Open_ShowsTheDefaultPlanningInstruction_WhenNoneIsSaved()
    {
        var vm = Open();

        vm.PlanningInstruction.Should().Be(PlanningInstruction.DefaultTemplate);
    }

    [Fact]
    public void Open_ShowsTheSavedPlanningInstruction()
    {
        _store.Settings = new AiSettings(@"C:\work", null, null, AiSettings.DefaultPermissionMode, null, "私の方針");

        Open().PlanningInstruction.Should().Be("私の方針");
    }

    [Fact]
    public void ResetPlanningInstruction_PutsTheDefaultBackWithoutSaving()
    {
        var vm = Open();
        vm.PlanningInstruction = "書きかけ";

        vm.ResetPlanningInstructionCommand.Execute(null);

        vm.PlanningInstruction.Should().Be(PlanningInstruction.DefaultTemplate);
        _store.SaveCalls.Should().BeEmpty();
    }

    /// <summary>既定と同じなら null のまま。既定の文面が改まったとき、書き換えていない人にも届く。</summary>
    [Fact]
    public void Save_StoresNull_WhenThePlanningInstructionIsTheDefault()
    {
        var vm = Open();
        vm.PlanningInstruction = "\n" + PlanningInstruction.DefaultTemplate + "  \n";

        vm.SaveCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.PlanningInstruction.Should().BeNull();
    }

    /// <summary>適用は指示文だけを差し替える。ほかの欄は保存済みの値のまま。</summary>
    [Fact]
    public void ApplyPlanningInstruction_ReplacesOnlyTheInstruction()
    {
        var vm = Open();
        vm.DefaultWorkingDirectory = "  ";
        vm.Model = "書きかけのモデル";
        vm.PlanningInstruction = "  私の方針  ";

        vm.ApplyPlanningInstructionCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.Should().Be(_store.Settings with { PlanningInstruction = "私の方針" });
        vm.StatusMessage.Should().Be(Strings.SettingsPlanningInstructionApplied);
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void ApplyPlanningInstruction_StoresNull_WhenItIsTheDefault()
    {
        _store.Settings = _store.Settings with { PlanningInstruction = "私の方針" };
        var vm = Open();

        vm.ResetPlanningInstructionCommand.Execute(null);
        vm.ApplyPlanningInstructionCommand.Execute(null);

        _store.SaveCalls.Should().ContainSingle().Which.PlanningInstruction.Should().BeNull();
    }

    [Fact]
    public void TemplateNotice_IsNullForAnOrdinaryTemplate()
    {
        var vm = new AiSettingsViewModel(new FakeAiSettingsStore());

        vm.TemplateNotice.Should().BeNull();

        vm.TerminalCommandTemplate = "powershell.exe -NoExit -Command {command}";
        vm.TemplateNotice.Should().BeNull();
    }

    /// <summary>計画づくりでは使えないテンプレートなので、保存前から画面で知らせる（仕様 §5.2）。</summary>
    [Fact]
    public void TemplateNotice_AppearsWhileTypingAWindowsTerminalTemplate()
    {
        var vm = new AiSettingsViewModel(new FakeAiSettingsStore());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TerminalCommandTemplate = "wt.exe -d \"{cwd}\" cmd /k {command}";

        vm.TemplateNotice.Should().Be(Strings.PlanTemplateFallsBackToDefault);
        raised.Should().Contain(nameof(AiSettingsViewModel.TemplateNotice));
    }
}
