using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Ai.MorningTools;
using MoTask.App.Resources;
using MoTask.App.Tests.Fakes;
using MoTask.Core;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// morning ツール 4 本（仕様 §6）。BoardToolHost のテストと同じ構えで、
/// 「引数 JSON → McpToolResult」だけを見る。
/// </summary>
public class MorningToolHostTests
{
    private readonly FakeMorningService _service = new();
    private readonly MorningToolHost _host;

    public MorningToolHostTests()
    {
        _host = new MorningToolHost(_service);
    }

    private async Task<(JsonElement Json, bool IsError, string Text)> CallAsync(string tool, string argumentsJson)
    {
        var target = _host.Tools.Single(t => t.Name == tool);
        using var doc = JsonDocument.Parse(argumentsJson);
        var result = await target.InvokeAsync(doc.RootElement, CancellationToken.None);
        return (result.IsError ? default : JsonDocument.Parse(result.Text).RootElement, result.IsError, result.Text);
    }

    [Fact]
    public void Tools_ExposesTheFourMorningTools_WithObjectSchemas()
    {
        _host.Tools.Select(t => t.Name).Should()
            .Equal("morning_get_context", "morning_add_candidate", "morning_submit_plan", "morning_complete");
        _host.Tools.Should().OnlyContain(t => t.Description.Length > 0);
        foreach (var tool in _host.Tools)
        {
            var schema = JsonSerializer.SerializeToElement(tool.InputSchema);
            schema.GetProperty("type").GetString().Should().Be("object");
            schema.GetProperty("required").EnumerateArray().Select(r => r.GetString())
                .Should().Contain("runId", "すべてのツールが runId を必須にする（仕様 §6）");
        }
    }

    [Fact]
    public async Task GetContext_ReturnsTheSnapshotVerbatim()
    {
        _service.ContextResult = Result.Ok("""{"date":"2026-09-13","tasks":[]}""");

        var (json, isError, _) = await CallAsync(MorningToolHost.GetContext, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("date").GetString().Should().Be("2026-09-13");
        _service.GetContextCalls.Should().Equal(7);
    }

    /// <summary>宛先違いはツールエラー。普段使いの Claude Code の誤爆を防ぐ（仕様 §6）。</summary>
    [Fact]
    public async Task GetContext_ReturnsAToolError_WhenTheRunIsNotRunning()
    {
        _service.ContextResult = Result.Fail<string>("runId 9999 の朝の実行は動いていません");

        var (_, isError, text) = await CallAsync(MorningToolHost.GetContext, """{"runId":9999}""");

        isError.Should().BeTrue();
        text.Should().Be("runId 9999 の朝の実行は動いていません");
        _service.GetContextCalls.Should().Equal(9999);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runId":"7"}""")]
    public async Task EveryTool_ReturnsAToolError_WhenRunIdIsMissingOrNotAnInteger(string arguments)
    {
        foreach (var tool in _host.Tools.Select(t => t.Name))
        {
            var (_, isError, text) = await CallAsync(tool, arguments);

            isError.Should().BeTrue(tool);
            text.Should().Be(Strings.McpMorningRunIdRequired, tool);
        }
    }

    [Fact]
    public async Task AddCandidate_PassesEveryFieldToTheService()
    {
        _service.AddCandidateResult = Result.Ok(new CandidateOutcome(true, null, 12, 3));

        var (json, isError, _) = await CallAsync(MorningToolHost.AddCandidate, """
            {"runId":7,"externalId":"outlook:001","source":"Outlook","title":"請求先情報を更新する",
             "evidence":"「9月8日までに」","suggestedAction":"merge","mergeTargetTaskId":45,
             "from":"山本さん","link":"https://x","reasoning":"依頼が明確",
             "receivedAt":"2026-09-13T07:42:00+09:00","suggestedDueDate":"2026-09-14",
             "suggestedProject":"顧客A"}
            """);

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        json.GetProperty("candidateId").GetInt32().Should().Be(12);
        json.GetProperty("total").GetInt32().Should().Be(3);

        var call = _service.AddCandidateCalls[^1];
        call.RunId.Should().Be(7);
        var input = call.Input;
        input.ExternalId.Should().Be("outlook:001");
        input.Source.Should().Be("Outlook");
        input.Evidence.Should().Be("「9月8日までに」");
        input.SuggestedAction.Should().Be("merge");
        input.MergeTargetTaskId.Should().Be(45);
        input.From.Should().Be("山本さん");
        input.Link.Should().Be("https://x");
        input.Reasoning.Should().Be("依頼が明確");
        input.ReceivedAt.Should().Be(new DateTime(2026, 9, 12, 22, 42, 0, DateTimeKind.Utc));
        input.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 14));
        input.SuggestedProject.Should().Be("顧客A");
    }

    [Fact]
    public async Task AddCandidate_TurnsMissingOptionalFieldsIntoEmptyStrings()
    {
        _service.AddCandidateResult = Result.Ok(new CandidateOutcome(true, null, 1, 1));

        await CallAsync(MorningToolHost.AddCandidate,
            """{"runId":7,"externalId":"x","source":"S","title":"T","evidence":"E","suggestedAction":"register"}""");

        var input = _service.AddCandidateCalls[^1].Input;
        input.From.Should().BeEmpty();
        input.Link.Should().BeEmpty();
        input.Reasoning.Should().BeEmpty();
        input.SuggestedProject.Should().BeEmpty();
        input.ReceivedAt.Should().BeNull();
        input.SuggestedDueDate.Should().BeNull();
        input.MergeTargetTaskId.Should().BeNull();
    }

    /// <summary>不備はツールエラーにせず、理由を通常の結果で返す（仕様 §3）。</summary>
    [Fact]
    public async Task AddCandidate_ReportsARefusalAsAnOrdinaryResult()
    {
        _service.AddCandidateResult = Result.Ok(
            new CandidateOutcome(false, "evidence が空です。元の文面から引用してください", 0, 2));

        var (json, isError, _) = await CallAsync(MorningToolHost.AddCandidate,
            """{"runId":7,"externalId":"x","source":"S","title":"T","evidence":"","suggestedAction":"register"}""");

        isError.Should().BeFalse("セッションを失敗させる話ではない");
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("evidence が空です。元の文面から引用してください");
    }

    [Fact]
    public async Task SubmitPlan_PassesTheRawPlanObject()
    {
        _service.SubmitPlanResult = Result.Ok(new MorningOutcome(true, null));

        var (json, isError, _) = await CallAsync(MorningToolHost.SubmitPlan,
            """{"runId":7,"plan":{"groups":[{"key":"today","items":[{"taskId":45}]}]}}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        _service.SubmitPlanCalls.Should().ContainSingle();
        _service.SubmitPlanCalls[0].RunId.Should().Be(7);
        _service.SubmitPlanCalls[0].PlanJson.Should()
            .Contain("\"key\":\"today\"").And.Contain("\"taskId\":45");
    }

    [Fact]
    public async Task SubmitPlan_ReturnsAToolError_WhenThePlanIsMissing()
    {
        var (_, isError, text) = await CallAsync(MorningToolHost.SubmitPlan, """{"runId":7}""");

        isError.Should().BeTrue();
        text.Should().Be(Strings.McpMorningPlanRequired);
    }

    [Fact]
    public async Task SubmitPlan_ReportsARefusalAsAnOrdinaryResult()
    {
        _service.SubmitPlanResult = Result.Ok(new MorningOutcome(false, "groups が配列ではありません"));

        var (json, isError, _) = await CallAsync(MorningToolHost.SubmitPlan, """{"runId":7,"plan":{}}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("groups が配列ではありません");
    }

    /// <summary>ツールの直後には閉じない。次の Stop まで待つ（仕様 §7）。</summary>
    [Fact]
    public async Task Complete_AsksTheServiceToReserveTheClose()
    {
        _service.CompleteRunResult = Result.Ok(new MorningOutcome(true, null));

        var (json, isError, _) = await CallAsync(MorningToolHost.Complete, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        _service.CompleteRunCalls.Should().ContainSingle();
        _service.CompleteRunCalls[0].RunId.Should().Be(7);
        _service.CompleteRunCalls[0].CloseNow.Should().BeFalse("ツールの直後には閉じない（仕様 §7）");
    }

    [Fact]
    public async Task Complete_ReportsAMissingPlanAsAnOrdinaryResult()
    {
        _service.CompleteRunResult = Result.Ok(
            new MorningOutcome(false, "先に morning_submit_plan を呼んでください"));

        var (json, isError, _) = await CallAsync(MorningToolHost.Complete, """{"runId":7}""");

        isError.Should().BeFalse();
        json.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("先に morning_submit_plan を呼んでください");
    }
}
