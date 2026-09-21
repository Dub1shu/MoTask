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
    /// <summary>
    /// ホイール 1 行ぶんの移動量。WPF の LineLeft/LineRight と同じ 16px。
    /// 列のカード一覧とは刻みが揃わない点に注意。あちらは CanContentScroll=True の論理スクロールで、
    /// 単位が px ではなく「件」なので、1 ノッチでカード 3 枚ぶん動く。
    /// </summary>
    private const double LineDelta = 16;

    /// <summary>端まで来たかの判定に使う許容差。レイアウトの丸めで半端な端数が残ることがある。</summary>
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

        // 横に余りが無いなら 1px も動かせない。食べずに他へ渡す。
        if (viewer.ScrollableWidth <= 0) return;
        if (!ShouldScrollBoard(e.OriginalSource as DependencyObject, viewer, e.Delta)) return;

        viewer.ScrollToHorizontalOffset(
            NextOffset(e.Delta, viewer.HorizontalOffset, viewer.ScrollableWidth,
                Step(SystemParameters.WheelScrollLines, viewer.ViewportWidth)));
        e.Handled = true;
    }

    /// <summary>ホイール 1 回ぶん動かした後の横位置。行き過ぎは端で止める。</summary>
    /// <param name="delta">MouseWheelEventArgs.Delta。負なら右へ、正なら左へ。0 は呼び出し側で弾く。</param>
    internal static double NextOffset(int delta, double current, double scrollableWidth, double step)
        => Math.Clamp(current + (delta < 0 ? step : -step), 0, Math.Max(0, scrollableWidth));

    /// <summary>ホイール 1 回で動かす量。OS の「1 度にスクロールする行数」に従う（-1 はページ送り）。</summary>
    internal static double Step(int wheelScrollLines, double viewportWidth)
        => wheelScrollLines < 0 ? viewportWidth : wheelScrollLines * LineDelta;

    /// <summary>
    /// このホイールをボードの横スクロールへ回してよいか。カーソル位置から
    /// <paramref name="outer"/> まで親をたどって決める。
    ///
    /// 途中で見つかる ScrollViewer のうち、いちばん内側のものだけを見る。バブリングなら実際に
    /// それがホイールを食うからで、もっと外側のものに譲っても内側が先に握りつぶして誰も動かない。
    /// </summary>
    internal static bool ShouldScrollBoard(DependencyObject? origin, ScrollViewer outer, int delta)
    {
        ScrollViewer? inner = null;
        for (var node = origin; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, outer))
            {
                // 間に何も挟まっていない（列ヘッダーや盤面の余白）か、挟まっていても
                // その向きへはもう動けない。どちらも横へ回してよい。
                return inner is null || !CanScrollVertically(inner, delta);
            }

            inner ??= node as ScrollViewer;
        }

        // outer に行き着かないまま親が尽きた。ポップアップ（ComboBox のドロップダウンなど）の
        // 中身は別ツリーに居るのでこうなる。裏のボードを動かすと操作と結果が食い違う。
        // 途中で ScrollViewer が見つかった時点で打ち切ってはいけない。ドロップダウンは
        // 自前の ScrollViewer を持っていて、それは縦に動けないので横に回されてしまう。
        return false;
    }

    /// <summary>この向きへまだ縦に動けるか。単位は px とは限らない（ListBox は「件」で数える）。</summary>
    private static bool CanScrollVertically(ScrollViewer viewer, int delta)
        => delta < 0
            ? viewer.VerticalOffset < viewer.ScrollableHeight - Epsilon
            : viewer.VerticalOffset > Epsilon;

    /// <summary>
    /// 親をたどる。OriginalSource はテンプレート内の部品のほか、TextBlock 内の Run のような
    /// Visual でない要素のこともあるので、その場合は論理ツリーへ逃がす。
    /// DragDropBehavior.ParentOf と同じ理由・同じ実装（片方だけ直さないこと）。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
