namespace MoTask.Core.Model;

/// <summary>
/// 受信箱から拾ったタスク候補1件（仕様 §9）。ExternalId が一意なので、
/// 一度片づけた候補は次の実行で Claude が再提出しても取り込み時に黙って捨てられる。
/// </summary>
public sealed class TriageCandidate
{
    public int Id { get; set; }
    public int PlanningRunId { get; set; }
    /// <summary>重複排除の鍵。一意インデックスを張る。</summary>
    public string ExternalId { get; set; } = "";
    /// <summary>取り込み元。enum ではなく自由文字列（仕様 §4）。</summary>
    public string Source { get; set; } = "";
    /// <summary>差出人。SQLite の予約語と同名だが、EF は識別子を必ず引用符で囲むので列名は "From" のままでよい。</summary>
    public string From { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>元の文面からの引用。これが無い候補はそもそも取り込まない（仕様 §8）。</summary>
    public string Evidence { get; set; } = "";
    public string Link { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public DateTime? ReceivedAt { get; set; }
    public DateOnly? SuggestedDueDate { get; set; }
    public string SuggestedProject { get; set; } = "";
    /// <summary>
    /// 推薦されたラベルの id（推薦の順）。名前でなく id で持つので、登録までに改名されても外れない。
    /// アーカイブされたものは登録時に落とす。
    /// </summary>
    public List<int> SuggestedLabelIds { get; set; } = new();
    public TriageAction SuggestedAction { get; set; } = TriageAction.Register;
    /// <summary>SuggestedAction が Merge のときの統合先。仕様 §8 の mergeTargetTaskId。</summary>
    public int? SuggestedMergeTaskId { get; set; }
    public TriageStatus Status { get; set; } = TriageStatus.Pending;
    /// <summary>登録先または統合先のタスク。</summary>
    public int? ResultTaskId { get; set; }
    public DateTime? DecidedAt { get; set; }
}
