using System.Globalization;
using System.Text.Json;
using MoTask.App.Ai.BoardTools;
using MoTask.Core.Morning;

namespace MoTask.App.Ai.MorningTools;

/// <summary>
/// morning_* の arguments を読む。BoardArgs に無いのは ISO8601 の receivedAt と
/// 生 JSON の取り出しだけなので、それだけを足して残りは BoardArgs に任せる
/// （仕様 §9: ホストは JSON ↔ ドメインの変換だけを持つ）。
/// </summary>
public readonly struct MorningArgs
{
    private readonly JsonElement _element;
    private readonly BoardArgs _args;

    public MorningArgs(JsonElement arguments)
    {
        _element = arguments;
        _args = new BoardArgs(arguments);
    }

    /// <summary>この朝の実行の runId。整数で来ていなければ null。</summary>
    public int? RunId => _args.Int("runId");

    /// <summary>オブジェクト／配列をそのままの文字列で取り出す（plan 用）。</summary>
    public string? Raw(string name)
        => _element.ValueKind == JsonValueKind.Object
           && _element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? value.GetRawText()
            : null;

    /// <summary>候補 1 件分。欠けた文字列は空文字にする（何が必須かは Core の純関数が決める）。</summary>
    public CandidateInput ToCandidateInput()
    {
        _args.TryDate("suggestedDueDate", out var due);
        return new CandidateInput(
            Text("externalId"), Text("source"), Text("title"), Text("evidence"),
            Text("from"), Text("link"), Text("reasoning"), Instant("receivedAt"),
            due, Text("suggestedProject"), Text("suggestedAction"), _args.Int("mergeTargetTaskId"));
    }

    private string Text(string name) => _args.String(name) ?? "";

    /// <summary>オフセット付き ISO8601 を UTC へ寄せる。読めなければ null（候補ごと捨てはしない）。</summary>
    private DateTime? Instant(string name)
    {
        var text = Text(name);
        return text.Length > 0
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.UtcDateTime
            : null;
    }
}
