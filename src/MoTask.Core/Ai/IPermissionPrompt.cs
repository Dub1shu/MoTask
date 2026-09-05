using MoTask.Core.Model;

namespace MoTask.Core.Ai;

/// <summary>人の答え。Remember が true なら Scope で AiPermissionRule を作る。</summary>
public sealed record HumanDecision(RuleDecision Decision, bool Remember, RuleScope Scope);

/// <summary>ダイアログに出す材料。RememberPattern は「今後も」で記憶されるパターン（null はツール全体）。</summary>
public sealed record PermissionPromptContext(AiJob Job, string TaskTitle, int? ProjectId, PermissionRequest Request, string? RememberPattern);

/// <summary>
/// AskHuman のときだけ呼ばれる。タイムアウトさせない（夜に放置して翌朝答えられるべき）。
/// ct はアプリ終了・ジョブ停止のときだけ取り消され、OperationCanceledException で抜ける。
/// </summary>
public interface IPermissionPrompt
{
    Task<HumanDecision> AskAsync(PermissionPromptContext context, CancellationToken ct);
}
