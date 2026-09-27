using System.Text.Encodings.Web;
using System.Text.Json;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Planning;
using MoTask.Core.Services;

namespace MoTask.App.Ai.PlanningTools;

/// <summary>
/// MCP の planning ツール 4 本（仕様 §6・§9）。IPlanningService だけを呼び、HTTP も JSON-RPC も
/// 知らない。検証は Core の純関数が持つので、ここは「JSON を読んで渡し、結果を JSON にする」だけ。
/// 説明文は resx に置かずここへ直書きする（MCP I/F 仕様 §3 の決定）。一方で利用者と Claude に
/// 返す理由は resx に置く。
/// </summary>
public sealed class PlanningToolHost
{
    public const string GetContext = "planning_get_context";
    public const string AddCandidate = "planning_add_candidate";
    public const string SubmitPlan = "planning_submit_plan";
    public const string Complete = "planning_complete";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IPlanningService _service;

    public PlanningToolHost(IPlanningService service)
    {
        _service = service;
        Tools = BuildTools();
    }

    public IReadOnlyList<McpTool> Tools { get; }

    // ---------- ツール定義 ----------

    private IReadOnlyList<McpTool> BuildTools() => new[]
    {
        new McpTool(GetContext,
            "今日の対象日と現在の盤面（列・未完了タスク・プロジェクト・期日・hasActiveAiJob）を返す。"
            + "統合先の推薦と今日の計画は、必ずこの結果に基づくこと。",
            new
            {
                type = "object",
                properties = new { runId = RunId() },
                required = new[] { "runId" },
            },
            GetContextAsync),

        new McpTool(AddCandidate,
            "この実行の候補を 1 件積む。1 件ずつ呼ぶこと。accepted が false で返ったら reason を読み、"
            + "直せるなら直して呼び直す。直せないなら、その候補は諦めて次へ進んでよい。",
            new
            {
                type = "object",
                properties = new
                {
                    runId = RunId(),
                    externalId = new
                    {
                        type = "string",
                        description = "再実行しても同じ値になる元の ID（例: outlook:AAMkAD001）。"
                            + "一度片づけた候補を二度出さないための鍵である。",
                    },
                    source = new { type = "string", description = "どこから拾ったか（Outlook / Teams / Gmail など）。" },
                    title = new { type = "string", description = "タスクにしたときの題名。" },
                    evidence = new { type = "string", description = "元の文面からの引用。根拠の無い候補は受け取らない。" },
                    suggestedAction = new
                    {
                        type = "string",
                        description = "推薦。決めるのは人なので、これは実行ではない。",
                        @enum = new[] { "register", "merge", "later", "reject" },
                    },
                    mergeTargetTaskId = new
                    {
                        type = "integer",
                        description = "suggestedAction が merge のときの統合先タスク id（planning_get_context の盤面にあるもの）。",
                    },
                    from = new { type = "string", description = "差出人。" },
                    link = new { type = "string", description = "元のメッセージへの URL。" },
                    reasoning = new { type = "string", description = "なぜ候補にしたか。" },
                    receivedAt = new { type = "string", description = "受信日時。ISO8601（オフセット付きでよい）。" },
                    suggestedDueDate = new { type = "string", description = "推薦する期日。YYYY-MM-DD。" },
                    suggestedProject = new { type = "string", description = "推薦するプロジェクト名。" },
                },
                required = new[] { "runId", "externalId", "source", "title", "evidence", "suggestedAction" },
            },
            AddCandidateAsync),

        new McpTool(SubmitPlan,
            "今日の計画を出す。何度でも呼べて、最後に受理されたものが残る。"
            + "accepted が false なら reason を読んで直し、呼び直すこと。",
            new
            {
                type = "object",
                properties = new
                {
                    runId = RunId(),
                    plan = new
                    {
                        type = "object",
                        description = "groups[] は key が today / ifTime / aiReady / waiting のいずれかで、"
                            + "items[] の各要素は taskId か externalId のどちらかを持つ。firstThing を置くなら同じ形にする。"
                            + "aiReady には planning_get_context の hasActiveAiJob が false で AI に任せられるものを入れる。",
                    },
                },
                required = new[] { "runId", "plan" },
            },
            SubmitPlanAsync),

        new McpTool(Complete,
            "この計画づくりはこれで終わり、と宣言する。先に planning_submit_plan を通しておくこと。"
            + "呼ぶとこの端末は閉じる。候補が 0 件の日でも必ず呼ぶこと（0 件は失敗ではない）。",
            new
            {
                type = "object",
                properties = new { runId = RunId() },
                required = new[] { "runId" },
            },
            CompleteAsync),
    };

    private static object RunId() => new
    {
        type = "integer",
        description = "この計画づくりの runId。instruction.md に書いてある値をそのまま渡すこと。",
    };

    // ---------- ハンドラ ----------

    private async Task<McpToolResult> GetContextAsync(JsonElement arguments, CancellationToken ct)
    {
        if (new PlanningArgs(arguments).RunId is not int runId) return MissingRunId();

        var context = await _service.GetContextAsync(runId, ct).ConfigureAwait(false);
        return context.IsSuccess ? McpToolResult.Ok(context.Value!) : McpToolResult.Error(context.Error!);
    }

    private async Task<McpToolResult> AddCandidateAsync(JsonElement arguments, CancellationToken ct)
    {
        var args = new PlanningArgs(arguments);
        if (args.RunId is not int runId) return MissingRunId();
        if (!args.TryToCandidateInput(out var input, out var reason))
        {
            return McpToolResult.Ok(Serialize(new { accepted = false, reason }));
        }

        var result = await _service.AddCandidateAsync(runId, input, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return McpToolResult.Error(result.Error!);

        var outcome = result.Value!;
        return McpToolResult.Ok(outcome.Accepted
            ? Serialize(new { accepted = true, candidateId = outcome.CandidateId, total = outcome.Total })
            : Serialize(new { accepted = false, reason = outcome.Reason }));
    }

    private async Task<McpToolResult> SubmitPlanAsync(JsonElement arguments, CancellationToken ct)
    {
        var args = new PlanningArgs(arguments);
        if (args.RunId is not int runId) return MissingRunId();
        if (args.Raw("plan") is not { } plan) return McpToolResult.Error(Strings.McpPlanningPlanRequired);

        return Answer(await _service.SubmitPlanAsync(runId, plan, ct).ConfigureAwait(false));
    }

    private async Task<McpToolResult> CompleteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (new PlanningArgs(arguments).RunId is not int runId) return MissingRunId();

        // その場では閉じない。ツール結果を返した直後に殺すと Claude の最後の一言が切れる（仕様 §7）。
        return Answer(await _service.CompleteRunAsync(runId, closeNow: false, ct).ConfigureAwait(false));
    }

    private static McpToolResult MissingRunId() => McpToolResult.Error(Strings.McpPlanningRunIdRequired);

    private static McpToolResult Answer(Result<PlanningOutcome> result)
    {
        if (!result.IsSuccess) return McpToolResult.Error(result.Error!);
        var outcome = result.Value!;
        return McpToolResult.Ok(outcome.Accepted
            ? Serialize(new { accepted = true })
            : Serialize(new { accepted = false, reason = outcome.Reason }));
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
