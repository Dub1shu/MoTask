namespace MoTask.Core.Model;

/// <summary>
/// ラベルに選べる色の見本。テーマ（Industry.xaml）の Brush.&lt;Hue&gt;.&lt;Step&gt; と 1 対 1 に対応する。
/// 管理ダイアログのポップアップは、この並びのまま 9 列 × 2 段で並べる。
/// </summary>
public static class LabelPalette
{
    private static readonly string[] Hues =
        { "red", "orange", "yellow", "green", "teal", "accent", "purple", "pink", "neutral" };

    /// <summary>新しいラベルに順に付ける色相。灰色は「色が無い」に見えるので外す。</summary>
    private static readonly string[] AutoHues =
        { "accent", "green", "orange", "purple", "red", "teal", "yellow", "pink" };

    public static IReadOnlyList<string> Colors { get; } =
        Hues.Select(h => $"{h}-300").Concat(Hues.Select(h => $"{h}-600")).ToArray();

    public static bool Contains(string? color)
        => color is not null && Colors.Contains(color.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>見本の色なら見本の表記（小文字）にそろえる。見本の外はそのまま返す。</summary>
    public static string Normalize(string color)
        => Colors.FirstOrDefault(c => string.Equals(c, color.Trim(), StringComparison.OrdinalIgnoreCase)) ?? color;

    public static string AutoColorFor(int index)
        => $"{AutoHues[((index % AutoHues.Length) + AutoHues.Length) % AutoHues.Length]}-300";
}
