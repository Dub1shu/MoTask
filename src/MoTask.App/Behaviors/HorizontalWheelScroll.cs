using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MoTask.App.Behaviors;

/// <summary>
/// 横に並ぶ ScrollViewer にマウスホイールでの横スクロールを足す添付ビヘイビア。
///
/// WPF の ScrollViewer はホイールを縦にしか配らない。ボードのように
/// VerticalScrollBarVisibility=Disabled にしていると、ホイールは
/// ScrollContentPresenter.MouseWheelDown() まで届いたあと何もせず、
/// e.Handled だけが立って終わる（＝ホイールが完全に死ぬ）。横へ読み替える口は
/// WPF 側に無いので、ここで塞ぐ。
///
/// トンネリングの PreviewMouseWheel で拾うのが要点。カードの上でホイールを回したときは
/// 列のカード一覧（ListBox 内部の ScrollViewer）がバブリングで先に食ってしまうため、
/// バブリングの MouseWheel では盤面のほとんどに届かない。
/// </summary>
public static class HorizontalWheelScroll
{
    /// <summary>ホイール 1 行ぶんの移動量。WPF が縦のホイールで使う刻みに合わせる。</summary>
    private const double LineDelta = 16;

    /// <summary>端かどうかの判定に使う許容差。レイアウトの丸めで 1px 未満の端数が残る。</summary>
    private const double Epsilon = 0.5;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HorizontalWheelScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ScrollViewer viewer) return;

        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) viewer.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        var viewer = (ScrollViewer)sender;

        // カーソルの真下に縦へ動ける入れ子があるなら、そちらを優先してホイールを素通しする。
        if (NearestInnerScrollViewer(e.OriginalSource as DependencyObject, viewer) is { } inner
            && CanScrollVertically(inner, e.Delta))
        {
            return;
        }

        viewer.ScrollToHorizontalOffset(
            NextOffset(e.Delta, viewer.HorizontalOffset, viewer.ScrollableWidth, Step(viewer.ViewportWidth)));
        e.Handled = true;
    }

    /// <summary>ホイール 1 回ぶん動かした後の横位置。行き過ぎは端で止める。</summary>
    internal static double NextOffset(int delta, double current, double scrollableWidth, double step)
        => Math.Clamp(current + (delta < 0 ? step : -step), 0, Math.Max(0, scrollableWidth));

    /// <summary>ホイール 1 回で動かす量。OS の「1 度にスクロールする行数」に従う（-1 はページ送り）。</summary>
    internal static double Step(double viewportWidth)
    {
        var lines = SystemParameters.WheelScrollLines;
        return lines < 0 ? viewportWidth : lines * LineDelta;
    }

    /// <summary>この向きへまだ縦に動けるか。動けないなら横に回してよい。</summary>
    private static bool CanScrollVertically(ScrollViewer viewer, int delta)
        => delta < 0
            ? viewer.VerticalOffset < viewer.ScrollableHeight - Epsilon
            : viewer.VerticalOffset > Epsilon;

    /// <summary>
    /// カーソル位置から <paramref name="outer"/> までの間にある、いちばん内側の ScrollViewer。
    /// 「いちばん内側」だけを見るのは、バブリングなら実際にそれがホイールを食うから。
    /// もっと外側のものに譲っても、内側が先に握りつぶすので誰も動かない。
    /// </summary>
    private static ScrollViewer? NearestInnerScrollViewer(DependencyObject? node, ScrollViewer outer)
    {
        while (node is not null && !ReferenceEquals(node, outer))
        {
            if (node is ScrollViewer inner) return inner;
            node = ParentOf(node);
        }

        return null;
    }

    /// <summary>
    /// 親をたどる。OriginalSource はテンプレート内の部品のほか、TextBlock 内の Run のような
    /// Visual でない要素のこともあるので、その場合は論理ツリーへ逃がす。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
