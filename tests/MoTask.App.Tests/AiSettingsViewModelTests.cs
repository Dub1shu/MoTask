using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using MoTask.Core.Services;
using NSubstitute;
using Xunit;

namespace MoTask.App.Tests;

public class AiSettingsViewModelTests
{
    private readonly IAiSettingsStore _store = Substitute.For<IAiSettingsStore>();
    private readonly IAiJobService _jobs = Substitute.For<IAiJobService>();
    private readonly List<AiPermissionRule> _rules = new();
    private readonly IReadOnlyList<Project> _projects = new[] { new Project { Id = 100, Name = "顧客A対応" } };

    public AiSettingsViewModelTests()
    {
        _store.Load().Returns(new AiSettings(@"C:\work", 2, @"C:\tools\claude.exe", "claude-sonnet-5", 30));
        _jobs.GetPermissionRulesAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<AiPermissionRule>>(_rules.ToList()));
        _jobs.DeletePermissionRuleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _rules.RemoveAll(r => r.Id == ci.Arg<int>());
            return Task.FromResult(Result.Ok());
        });
    }

    private async Task<AiSettingsViewModel> OpenAsync()
    {
        var vm = new AiSettingsViewModel(_store, _jobs, _projects);
        await vm.PendingLoad;
        return vm;
    }

    [Fact]
    public async Task Open_LoadsSettingsIntoFields()
    {
        var vm = await OpenAsync();

        vm.DefaultWorkingDirectory.Should().Be(@"C:\work");
        vm.MaxConcurrentText.Should().Be("2");
        vm.ClaudeExecutablePath.Should().Be(@"C:\tools\claude.exe");
        vm.Model.Should().Be("claude-sonnet-5");
        vm.MaxTurnsText.Should().Be("30");
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Save_WritesTrimmedValues_AndBlanksBecomeNull()
    {
        var vm = await OpenAsync();
        vm.DefaultWorkingDirectory = @"  D:\ai  ";
        vm.MaxConcurrentText = " 4 ";
        vm.ClaudeExecutablePath = "   ";
        vm.Model = "";
        vm.MaxTurnsText = "25";

        vm.SaveCommand.Execute(null);

        _store.Received(1).Save(new AiSettings(@"D:\ai", 4, null, null, 25));
        vm.StatusMessage.Should().Be(Strings.SettingsSaved);
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Save_RejectsNonPositiveOrNonNumericLimits()
    {
        var vm = await OpenAsync();

        vm.MaxConcurrentText = "0";
        vm.SaveCommand.Execute(null);
        vm.ErrorMessage.Should().Be(Strings.MaxConcurrentMustBePositive);

        vm.MaxConcurrentText = "3";
        vm.MaxTurnsText = "abc";
        vm.SaveCommand.Execute(null);
        vm.ErrorMessage.Should().Be(Strings.MaxTurnsMustBePositive);

        vm.MaxTurnsText = "10";
        vm.DefaultWorkingDirectory = "  ";
        vm.SaveCommand.Execute(null);
        vm.ErrorMessage.Should().Be(Strings.DefaultWorkingDirectoryRequired);

        _store.DidNotReceive().Save(Arg.Any<AiSettings>());
    }

    /// <summary>上限が無いと 1000 件の claude 子プロセスを許してしまう。</summary>
    [Fact]
    public async Task Save_RejectsAConcurrencyLimitAboveTheMaximum()
    {
        var vm = await OpenAsync();
        vm.MaxConcurrentText = "21";

        vm.SaveCommand.Execute(null);

        vm.ErrorMessage.Should().Be(string.Format(Strings.MaxConcurrentTooLargeFormat, 20));
        vm.StatusMessage.Should().BeNull();
        _store.DidNotReceive().Save(Arg.Any<AiSettings>());
    }

    [Fact]
    public async Task Save_AcceptsTheMaximumConcurrencyLimit()
    {
        var vm = await OpenAsync();
        vm.MaxConcurrentText = "20";

        vm.SaveCommand.Execute(null);

        vm.ErrorMessage.Should().BeNull();
        _store.Received(1).Save(new AiSettings(@"C:\work", 20, @"C:\tools\claude.exe", "claude-sonnet-5", 30));
    }

    [Fact]
    public async Task Rules_AreListed_WithDecisionPatternAndScope()
    {
        _rules.Add(new AiPermissionRule { Id = 1, Scope = RuleScope.Project, ProjectId = 100, ToolName = "Bash", Pattern = "git push", Decision = RuleDecision.Allow });
        _rules.Add(new AiPermissionRule { Id = 2, Scope = RuleScope.Global, ToolName = "WebFetch", Pattern = null, Decision = RuleDecision.Deny });
        _rules.Add(new AiPermissionRule { Id = 3, Scope = RuleScope.Project, ProjectId = 999, ToolName = "Write", Pattern = @"C:\w", Decision = RuleDecision.Allow });

        var vm = await OpenAsync();

        vm.Rules.Select(r => r.Text).Should().Equal(
            string.Format(Strings.SettingsRuleFormat, Strings.SettingsRuleAllow, "Bash「git push」", string.Format(Strings.SettingsScopeProjectFormat, "顧客A対応")),
            string.Format(Strings.SettingsRuleFormat, Strings.SettingsRuleDeny, "WebFetch（" + Strings.SettingsRuleAllTool + "）", Strings.SettingsScopeGlobal),
            string.Format(Strings.SettingsRuleFormat, Strings.SettingsRuleAllow, @"Write「C:\w」", string.Format(Strings.SettingsScopeProjectFormat, "?")));
    }

    [Fact]
    public async Task DeleteRule_CallsService_AndReloads()
    {
        _rules.Add(new AiPermissionRule { Id = 1, Scope = RuleScope.Global, ToolName = "Bash", Pattern = "git push", Decision = RuleDecision.Allow });
        var vm = await OpenAsync();

        await vm.DeleteRuleCommand.ExecuteAsync(vm.Rules.Single());

        await _jobs.Received(1).DeletePermissionRuleAsync(1, Arg.Any<CancellationToken>());
        vm.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRule_Failure_ShowsError()
    {
        _rules.Add(new AiPermissionRule { Id = 1, Scope = RuleScope.Global, ToolName = "Bash", Decision = RuleDecision.Allow });
        _jobs.DeletePermissionRuleAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Fail(Messages.PermissionRuleNotFound)));
        var vm = await OpenAsync();

        await vm.DeleteRuleCommand.ExecuteAsync(vm.Rules.Single());

        vm.ErrorMessage.Should().Be(Messages.PermissionRuleNotFound);
    }
}
