using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MoTask.App.Behaviors;

/// <summary>
/// 横に並ぶ ScrollViewer を、Shift + ホイールと横ホイール付きマウスで横に流す添付ビヘイビア。
///
/// WPF が用意しているのは縦のホイールだけで、しかもボードのように
/// VerticalScrollBarVisibility=Disabled にしていると、ホイールは
/// ScrollContentPresenter.MouseWheelDown() まで届いたあと何もせず e.Handled だけ立てて終わる。
/// 横へ動かす手立てが無いので、ここで足す。
///
/// 足すのは横へ動かすつもりの操作だけ。普通に縦へ回したホイールには触らない。
/// 触ると、縦のつもりで回したときに盤面が横へ流れて操作と結果が食い違う。
///
/// - Shift + ホイール: WPF のルーテッドイベントで受ける。トンネリングの PreviewMouseWheel で
///   拾うのが要点で、バブリングだと列のカード一覧が先に食ってしまい盤面のほとんどに届かない。
/// - 横ホイール（チルトホイールなど）: Windows は WM_MOUSEHWHEEL を送るが、WPF はこれを
///   まったくルーティングしない。ウィンドウメッセージのまま受けるほかない。
/// </summary>
public static class HorizontalWheelScroll
{
    /// <summary>
    /// ホイール 1 行ぶんの移動量。WPF の LineLeft/LineRight と同じ 16px。
    /// 列のカード一覧とは刻みが揃わない点に注意。あちらは CanContentScroll=True の論理スクロールで、
    /// 単位が px ではなく「件」なので、1 ノッチでカード 3 枚ぶん動く。
    /// </summary>
    private const double LineDelta = 16;

    /// <summary>横ホイールのウィンドウメッセージ。WPF はこれをルーティングしない。</summary>
    internal const int WmMouseHWheel = 0x020E;

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

        // 付け直されても二重に購読しないよう、必ず外してから付ける。
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.Loaded -= OnLoaded;
        viewer.Unloaded -= OnUnloaded;
        DetachWindowHook(viewer);

        if (e.NewValue is not true) return;

        viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        viewer.Loaded += OnLoaded;
        viewer.Unloaded += OnUnloaded;
        if (viewer.IsLoaded) AttachWindowHook(viewer);
    }

    // ---- Shift + ホイール ----

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var viewer = (ScrollViewer)sender;

        var amount = AmountForVerticalWheel(e.Delta, Keyboard.Modifiers, StepFor(viewer));
        if (TryScroll(viewer, amount, e.OriginalSource as DependencyObject)) e.Handled = true;
    }

    /// <summary>
    /// 縦ホイールを横の移動量に読み替える。Shift を押していないときは 0（＝何もしない）。
    /// </summary>
    /// <param name="delta">MouseWheelEventArgs.Delta。手前へ回すと負。</param>
    internal static double AmountForVerticalWheel(int delta, ModifierKeys modifiers, double step)
    {
        if ((modifiers & ModifierKeys.Shift) == 0) return 0;
        return delta < 0 ? step : delta > 0 ? -step : 0;
    }

    // ---- 横ホイール（WM_MOUSEHWHEEL） ----

    /// <summary>
    /// 横ホイールの回転量を横の移動量に読み替える。倒した向きへそのまま動かす。
    /// </summary>
    /// <param name="delta">WM_MOUSEHWHEEL の回転量。右へ倒すと正で、縦ホイールとは向きが逆。</param>
    internal static double AmountForHorizontalWheel(int delta, double step)
        => delta > 0 ? step : delta < 0 ? -step : 0;

    /// <summary>WM_MOUSEHWHEEL の wParam から回転量を取り出す（上位ワードを符号付きで読む）。</summary>
    internal static int HorizontalWheelDelta(IntPtr wParam)
        => (short)(((long)wParam >> 16) & 0xFFFF);

    private static void OnLoaded(object sender, RoutedEventArgs e) => AttachWindowHook((ScrollViewer)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => DetachWindowHook((ScrollViewer)sender);

    /// <summary>解除できるよう、掛けたフックと掛け先を持っておく。</summary>
    private sealed record Hooked(HwndSource Source, HwndSourceHook Hook);

    private static readonly DependencyProperty WindowHookProperty = DependencyProperty.RegisterAttached(
        "WindowHook", typeof(Hooked), typeof(HorizontalWheelScroll));

    private static void AttachWindowHook(ScrollViewer viewer)
    {
        if (viewer.GetValue(WindowHookProperty) is Hooked) return;
        if (PresentationSource.FromVisual(viewer) is not HwndSource source) return;

        IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmMouseHWheel) return IntPtr.Zero;

            // メッセージはフォーカスのあるウィンドウに届くだけなので、カーソルがボードの上に
            // あるかはこちらで確かめる。Mouse.DirectlyOver がカーソル直下の要素。
            var amount = AmountForHorizontalWheel(HorizontalWheelDelta(wParam), StepFor(viewer));
            if (TryScroll(viewer, amount, Mouse.DirectlyOver as DependencyObject)) handled = true;
            return IntPtr.Zero;
        }

        var hook = new HwndSourceHook(Hook);
        source.AddHook(hook);
        viewer.SetValue(WindowHookProperty, new Hooked(source, hook));
    }

    private static void DetachWindowHook(ScrollViewer viewer)
    {
        if (viewer.GetValue(WindowHookProperty) is not Hooked hooked) return;

        // 掛け先を覚えておくのは、Unloaded の時点では PresentationSource が既に外れていて
        // そこから辿り直せないため。
        hooked.Source.RemoveHook(hooked.Hook);
        viewer.ClearValue(WindowHookProperty);
    }

    // ---- 実際に動かす ----

    /// <summary>
    /// ボードを <paramref name="amount"/> だけ横へ動かす。動かせたら true。
    /// 1px も動かせないときに true を返さないのは、食べたイベントを他所へ渡すため。
    /// </summary>
    internal static bool TryScroll(ScrollViewer viewer, double amount, DependencyObject? origin)
    {
        if (amount == 0 || viewer.ScrollableWidth <= 0) return false;
        if (!IsInsideBoard(origin, viewer)) return false;

        viewer.ScrollToHorizontalOffset(NextOffset(viewer.HorizontalOffset, viewer.ScrollableWidth, amount));
        return true;
    }

    /// <summary>動かした後の横位置。行き過ぎは端で止める。</summary>
    internal static double NextOffset(double current, double scrollableWidth, double amount)
        => Math.Clamp(current + amount, 0, Math.Max(0, scrollableWidth));

    /// <summary>ホイール 1 回で動かす量。OS の「1 度にスクロールする行数」に従う（-1 はページ送り）。</summary>
    internal static double Step(int wheelScrollLines, double viewportWidth)
        => wheelScrollLines < 0 ? viewportWidth : wheelScrollLines * LineDelta;

    private static double StepFor(ScrollViewer viewer)
        => Step(SystemParameters.WheelScrollLines, viewer.ViewportWidth);

    /// <summary>
    /// カーソル直下の要素からボードまで親をたどれるか。ポップアップ（ComboBox のドロップダウン
    /// など）の中身は別のビジュアルツリーに居るので、親が尽きてボードに行き着かない。
    /// そこで動かすと、ドロップダウンを操作しているのに裏の盤面が流れることになる。
    /// </summary>
    internal static bool IsInsideBoard(DependencyObject? origin, ScrollViewer board)
    {
        for (var node = origin; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, board)) return true;
        }

        return false;
    }

    /// <summary>
    /// 親をたどる。OriginalSource はテンプレート内の部品のほか、TextBlock 内の Run のような
    /// Visual でない要素のこともあるので、その場合は論理ツリーへ逃がす。
    /// DragDropBehavior.ParentOf と同じ理由・同じ実装（片方だけ直さないこと）。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
