using System.Windows;
using System.Windows.Controls;

namespace MoTask.App.DragDrop;

/// <summary>
/// 実体化済みの項目コンテナの位置から「カーソルはどの項目の手前か」を決める純粋な計算。
/// UI スレッドも WPF の制御も要らないので、そのまま単体テストできる。
/// </summary>
public static class InsertIndexCalculator
{
    /// <param name="containers">
    /// 実体化済みコンテナの (ItemsSource 上の添字, ドロップ先要素から見た矩形)。添字の昇順であること。
    /// ListBox は項目を仮想化するので、画面外の項目はここに現れない。
    /// </param>
    /// <param name="cursor">ドロップ先要素から見たカーソル位置。</param>
    /// <returns>
    /// この添字の項目の手前に挿す、という意味の値。末尾なら最後の項目の添字 + 1。
    /// gong の InsertIndex と同じ意味づけで、DropPositionCalculator がこれを前提にする。
    /// </returns>
    public static int Calculate(
        IReadOnlyList<(int Index, Rect Bounds)> containers,
        Point cursor,
        Orientation orientation)
    {
        if (containers.Count == 0) return 0;

        var position = orientation == Orientation.Vertical ? cursor.Y : cursor.X;
        foreach (var (index, bounds) in containers)
        {
            var middle = orientation == Orientation.Vertical
                ? bounds.Top + bounds.Height / 2
                : bounds.Left + bounds.Width / 2;
            if (position < middle) return index;
        }

        return containers[^1].Index + 1;
    }
}
