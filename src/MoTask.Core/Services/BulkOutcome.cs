namespace MoTask.Core.Services;

/// <summary>一括操作の結果（仕様 §5）。Skipped は「候補のタイトル: 理由」の形で画面にそのまま出す。</summary>
public sealed record BulkOutcome(int Applied, IReadOnlyList<string> Skipped);
