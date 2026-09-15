namespace MoTask.Core.Morning;

/// <summary>
/// 候補 1 件の受け口の返事（仕様 §6）。Accepted が false でもツールエラーにはしない。
/// Claude は「1 件弾かれた」だけを受け取って次へ進めばよい（仕様 §3）。
/// Total は受理後にこの実行へ積まれている候補の件数。
/// </summary>
public sealed record CandidateOutcome(bool Accepted, string? Reason, int CandidateId, int Total);

/// <summary>プラン提出・完了宣言の返事。同じく Accepted が false でもツールエラーにはしない。</summary>
public sealed record MorningOutcome(bool Accepted, string? Reason);
