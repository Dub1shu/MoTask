using FluentAssertions;
using MoTask.Core.Ai;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.Core.Tests;

public class PermissionPolicyTests
{
    private readonly PermissionPolicy _policy = new();

    private static PermissionRequest Bash(string command)
        => new("Bash", $$$"""{"command":{{{System.Text.Json.JsonSerializer.Serialize(command)}}},"description":"x"}""", "toolu_1");

    private static PermissionRequest Write(string path)
        => new("Write", $$$"""{"file_path":{{{System.Text.Json.JsonSerializer.Serialize(path)}}},"content":"hello"}""", "toolu_2");

    private static AiPermissionRule Rule(string tool, string? pattern, RuleDecision decision, RuleScope scope = RuleScope.Global, int? projectId = null)
        => new() { ToolName = tool, Pattern = pattern, Decision = decision, Scope = scope, ProjectId = projectId, CreatedAt = DateTime.UtcNow };

    // ---- パターンの作り方 ----

    [Fact]
    public void ForRemembering_Bash_TakesFirstTwoTokens()
    {
        PermissionPattern.ForRemembering(Bash("git push origin main")).Should().Be("git push");
        PermissionPattern.ForRemembering(Bash("dotnet")).Should().Be("dotnet");
        PermissionPattern.ForRemembering(Bash("  dotnet   build  -c Release ")).Should().Be("dotnet build");
    }

    [Fact]
    public void ForRemembering_WriteAndEdit_TakeTheDirectory()
    {
        PermissionPattern.ForRemembering(Write(@"C:\work\proj\report.md")).Should().Be(@"C:\work\proj");
        var edit = new PermissionRequest("Edit", """{"file_path":"C:\\work\\proj\\a\\b.cs","old_string":"x","new_string":"y"}""", null);
        PermissionPattern.ForRemembering(edit).Should().Be(@"C:\work\proj\a");
    }

    [Fact]
    public void ForRemembering_OtherTools_IsNull()
    {
        PermissionPattern.ForRemembering(new PermissionRequest("WebFetch", """{"url":"https://x"}""", null)).Should().BeNull();
    }

    [Fact]
    public void Subject_ReturnsCommandOrPath_AndNullForBrokenInput()
    {
        PermissionPattern.Subject(Bash("echo hi")).Should().Be("echo hi");
        PermissionPattern.Subject(Write(@"C:\a\b.txt")).Should().Be(@"C:\a\b.txt");
        PermissionPattern.Subject(new PermissionRequest("Bash", "{not json", null)).Should().BeNull();
        PermissionPattern.Subject(new PermissionRequest("Read", """{"file_path":"x"}""", null)).Should().BeNull();
    }

    // ---- 一致条件 ----

    [Fact]
    public void Matches_Bash_RequiresTokenBoundary()
    {
        var rule = Rule("Bash", "git push", RuleDecision.Allow);
        PermissionPattern.Matches(rule, Bash("git push origin main")).Should().BeTrue();
        PermissionPattern.Matches(rule, Bash("git push")).Should().BeTrue();
        PermissionPattern.Matches(rule, Bash("git pushx")).Should().BeFalse("トークン境界で区切る");
        PermissionPattern.Matches(rule, Bash("git pull")).Should().BeFalse();
        PermissionPattern.Matches(rule, Write(@"C:\x")).Should().BeFalse("ツール名が違う");
    }

    [Fact]
    public void Matches_Write_RequiresFileUnderDirectory_CaseInsensitive()
    {
        var rule = Rule("Write", @"C:\work\proj", RuleDecision.Allow);
        PermissionPattern.Matches(rule, Write(@"C:\work\proj\report.md")).Should().BeTrue();
        PermissionPattern.Matches(rule, Write(@"c:\WORK\proj\sub\deep.md")).Should().BeTrue();
        PermissionPattern.Matches(rule, Write(@"C:\work\proj2\x.md")).Should().BeFalse("前方一致ではなくディレクトリ配下");
        PermissionPattern.Matches(rule, Write(@"C:\work\proj\..\other\x.md")).Should().BeFalse("正規化してから比べる");
    }

    [Fact]
    public void Matches_NullPattern_MatchesWholeTool()
    {
        var rule = Rule("WebFetch", null, RuleDecision.Deny);
        PermissionPattern.Matches(rule, new PermissionRequest("WebFetch", """{"url":"https://x"}""", null)).Should().BeTrue();
        PermissionPattern.Matches(rule, new PermissionRequest("WebSearch", """{"query":"x"}""", null)).Should().BeFalse();
        PermissionPattern.Matches(Rule("Bash", null, RuleDecision.Allow), Bash("anything at all")).Should().BeTrue();
    }

    // ---- 照合順序 ----

    [Fact]
    public void Evaluate_NoMatchingRule_AsksHuman()
    {
        _policy.Evaluate(null, Array.Empty<AiPermissionRule>(), Bash("rm -rf x")).Should().Be(PolicyVerdict.AskHuman);
        _policy.Evaluate(null, new[] { Rule("Bash", "git push", RuleDecision.Allow) }, Bash("git pull")).Should().Be(PolicyVerdict.AskHuman);
    }

    [Fact]
    public void Evaluate_DenyBeatsAllow_WithinAScope()
    {
        var rules = new[] { Rule("Bash", "git", RuleDecision.Allow), Rule("Bash", "git push", RuleDecision.Deny) };
        _policy.Evaluate(null, rules, Bash("git push origin")).Should().Be(PolicyVerdict.Deny);
        _policy.Evaluate(null, rules, Bash("git status")).Should().Be(PolicyVerdict.Allow);
    }

    [Fact]
    public void Evaluate_ProjectScopeBeatsGlobal()
    {
        var rules = new[]
        {
            Rule("Bash", "git push", RuleDecision.Deny, RuleScope.Global),
            Rule("Bash", "git push", RuleDecision.Allow, RuleScope.Project, projectId: 7),
        };
        _policy.Evaluate(7, rules, Bash("git push")).Should().Be(PolicyVerdict.Allow, "プロジェクトの Allow が全体の Deny に勝つ");
        _policy.Evaluate(8, rules, Bash("git push")).Should().Be(PolicyVerdict.Deny, "別プロジェクトには効かない");
        _policy.Evaluate(null, rules, Bash("git push")).Should().Be(PolicyVerdict.Deny, "プロジェクト無しのタスクには Global だけが効く");
    }
}
