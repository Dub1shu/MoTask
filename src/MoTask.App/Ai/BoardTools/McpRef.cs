using System.Globalization;

namespace MoTask.App.Ai.BoardTools;

/// <summary>
/// 列・プロジェクト・ラベルの指定。id（整数）でも名前（文字列）でもよい（仕様 §6「共通の約束」）。
/// どちらか一方だけが入る。
/// </summary>
public readonly record struct McpRef(int? Id, string? Name)
{
    public static McpRef FromId(int id) => new(id, null);

    public static McpRef FromName(string name) => new(null, name);

    /// <summary>エラーメッセージに出す表記。</summary>
    public override string ToString()
        => Id is int id ? id.ToString(CultureInfo.InvariantCulture) : Name ?? "";
}
