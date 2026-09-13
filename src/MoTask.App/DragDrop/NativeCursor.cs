using System.Runtime.InteropServices;

namespace MoTask.App.DragDrop;

/// <summary>
/// ドラッグ中のカーソル位置。WPF の GiveFeedback は DragEventArgs を渡してくれないので、
/// 画面座標を Win32 から直接取る。ドラッグのゴーストを追従させるためだけに使う。
/// </summary>
internal static class NativeCursor
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);
}
