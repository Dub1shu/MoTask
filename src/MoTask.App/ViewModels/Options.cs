using System.Globalization;
using MoTask.Core.Filtering;
using MoTask.Core.Model;

namespace MoTask.App.ViewModels;

public sealed record ProjectOption(int? Id, string Name);

public sealed record DueOption(DueFilter Value, string Name);

/// <summary>列を追加するときに選ぶ種別。<c>Done</c> は1列だけなので選択肢に入れない（仕様 §5）。</summary>
public sealed record ColumnRoleOption(ColumnRole Value, string Name);

/// <summary>
/// カードや詳細パネルに出すラベルのチップ。<paramref name="Color"/> は背景に使うランプ段の名前で、
/// <see cref="TextColor"/> はその段の上で読める文字色のランプ段。
/// </summary>
public sealed record LabelChip(int Id, string Name, string Color)
{
    public string TextColor { get; } = RampSteps.TextColorOn(Color);
}

/// <summary>ランプ名（"accent-500" など）の段の濃さを読む。</summary>
public static class RampSteps
{
    /// <summary>濃い段の上に載せる明るい文字。</summary>
    public const string LightText = "neutral-100";

    /// <summary>明るい段の上に載せる濃い文字。</summary>
    public const string DarkText = "neutral-900";

    /// <summary>段が読めないときに仮定する濃さ。Label の既定色 accent-300 と揃える。</summary>
    private const int DefaultStep = 300;

    /// <summary>ここから上の段は暗いので、明るい文字のほうがコントラストが取れる。</summary>
    private const int DarkFromStep = 600;

    /// <summary>その段を背景にしたときに読める文字色のランプ名を返す。</summary>
    public static string TextColorOn(string? color)
        => StepOf(color) >= DarkFromStep ? LightText : DarkText;

    private static int StepOf(string? color)
    {
        if (color is null) return DefaultStep;
        var dash = color.LastIndexOf('-');
        if (dash < 0) return DefaultStep;
        return int.TryParse(color[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var step)
            ? step
            : DefaultStep;
    }
}
