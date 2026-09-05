using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class PermissionDialogViewModelTests
{
    private static readonly AiJob Job = new() { Id = 1, TaskId = 10, Kind = AiJobKind.Execute, WorkingDirectory = @"C:\work\proj" };

    private static PermissionPromptContext Context(string tool, string inputJson, int? projectId = 100)
    {
        var request = new PermissionRequest(tool, inputJson, "toolu_1");
        return new PermissionPromptContext(Job, "請求先情報を更新する", projectId, request, PermissionPattern.ForRemembering(request));
    }

    [Fact]
    public void Bash_ShowsCommandAsSummary_AndDescriptionAsDetail()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"git push origin main","description":"Push"}"""));

        vm.ToolLine.Should().Be(string.Format(Strings.PermissionToolFormat, "Bash"));
        vm.Summary.Should().Be("git push origin main");
        vm.Detail.Should().Be("Push");
        vm.TaskTitle.Should().Be("請求先情報を更新する");
        vm.WorkingDirectory.Should().Be(@"C:\work\proj");
        vm.RememberText.Should().Be(string.Format(Strings.PermissionRememberFormat, string.Format(Strings.PermissionRememberBashFormat, "git push")));
        vm.HasProject.Should().BeTrue();
        vm.RememberForProject.Should().BeTrue("プロジェクトがあれば既定はプロジェクト限定");
        vm.RememberForAll.Should().BeFalse();
    }

    [Fact]
    public void Write_ShowsPathAsSummary_AndContentAsDetail()
    {
        var vm = new PermissionDialogViewModel(Context("Write", """{"file_path":"C:\\work\\proj\\out.md","content":"# 見出し\nhello"}"""));

        vm.Summary.Should().Be(@"C:\work\proj\out.md");
        vm.Detail.Should().Be("# 見出し\nhello");
        vm.RememberText.Should().Be(string.Format(Strings.PermissionRememberFormat, string.Format(Strings.PermissionRememberDirFormat, @"C:\work\proj")));
    }

    [Fact]
    public void Edit_ShowsOldAndNew()
    {
        var vm = new PermissionDialogViewModel(Context("Edit", """{"file_path":"C:\\w\\a.cs","old_string":"foo","new_string":"bar"}"""));

        vm.Summary.Should().Be(@"C:\w\a.cs");
        vm.Detail.Should().Be(string.Format(Strings.PermissionEditFormat, "foo", "bar"));
    }

    [Fact]
    public void OtherTool_ShowsToolNameAndPrettyJson_AndRemembersWholeTool()
    {
        var vm = new PermissionDialogViewModel(Context("WebFetch", """{"url":"https://example.com","prompt":"x"}"""));

        vm.Summary.Should().Be("WebFetch");
        vm.Detail.Should().Contain("\"url\": \"https://example.com\"");
        vm.RememberText.Should().Be(string.Format(Strings.PermissionRememberFormat, string.Format(Strings.PermissionRememberToolOnly, "WebFetch")));
    }

    [Fact]
    public void BrokenInput_FallsBackToRawText()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", "{not json"));
        vm.Summary.Should().Be("Bash");
        vm.Detail.Should().Be("{not json");
    }

    [Fact]
    public void Allow_DoesNotRemember_AndCloses()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"ls"}"""));
        var closed = false;
        vm.Closed += () => closed = true;

        vm.AllowCommand.Execute(null);

        vm.Decision.Should().Be(new HumanDecision(RuleDecision.Allow, false, RuleScope.Global));
        closed.Should().BeTrue();
    }

    [Fact]
    public void Deny_DoesNotRemember()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"ls"}"""));
        vm.DenyCommand.Execute(null);
        vm.Decision.Should().Be(new HumanDecision(RuleDecision.Deny, false, RuleScope.Global));
    }

    [Fact]
    public void AllowAlways_WithProjectScope_RemembersForProject()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"git push"}"""));
        vm.AllowAlwaysCommand.Execute(null);
        vm.Decision.Should().Be(new HumanDecision(RuleDecision.Allow, true, RuleScope.Project));
    }

    [Fact]
    public void DenyAlways_WithAllScope_RemembersGlobally()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"git push"}"""));
        vm.RememberForAll = true;

        vm.RememberForProject.Should().BeFalse("排他");
        vm.DenyAlwaysCommand.Execute(null);
        vm.Decision.Should().Be(new HumanDecision(RuleDecision.Deny, true, RuleScope.Global));
    }

    [Fact]
    public void WithoutProject_ScopeIsAlwaysGlobal()
    {
        var vm = new PermissionDialogViewModel(Context("Bash", """{"command":"git push"}""", projectId: null));

        vm.HasProject.Should().BeFalse();
        vm.RememberForProject.Should().BeFalse();
        vm.RememberForAll.Should().BeTrue();
        vm.AllowAlwaysCommand.Execute(null);
        vm.Decision!.Scope.Should().Be(RuleScope.Global);
    }
}
