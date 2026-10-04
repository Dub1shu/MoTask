using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MoTask.App.DragDrop;

/// <summary>落ちる場所を示す線 1 本。ドラッグ中だけ AdornerLayer に載る。</summary>
public sealed class InsertionAdorner : Adorner
{
    private readonly Orientation _orientation;
    private readonly Pen _pen;
    private Rect? _target;
    private bool _after;

    public InsertionAdorner(UIElement adornedElement, Orientation orientation)
        : base(adornedElement)
    {
        _orientation = orientation;
        _pen = CreatePen(Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush);
        IsHitTestVisible = false;
    }

    /// <param name="target">線を引く基準にする項目の矩形。</param>
    /// <param name="after">true なら項目の後ろ側（下端 / 右端）に引く。</param>
    public void MoveTo(Rect target, bool after)
    {
        _target = target;
        _after = after;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_target is not { } target) return;

        if (_orientation == Orientation.Vertical)
        {
            var y = _after ? target.Bottom : target.Top;
            drawingContext.DrawLine(_pen, new Point(target.Left, y), new Point(target.Right, y));
        }
        else
        {
            var x = _after ? target.Right : target.Left;
            drawingContext.DrawLine(_pen, new Point(x, target.Top), new Point(x, target.Bottom));
        }
    }

    /// <summary>
    /// 線の色はテーマのアクセント色。Adorner はドラッグのたびに作られるので、ここで引けば
    /// OS のテーマやアクセント色を切り替えたあとの次のドラッグから追従する。見つからなければ既定色で描く。
    /// </summary>
    internal static Pen CreatePen(Brush? themeBrush)
    {
        var brush = themeBrush ?? new SolidColorBrush(Color.FromRgb(0x4C, 0x8E, 0xFF));
        // Fluent のブラシは色がアクセント色への動的参照で、凍結できない（そのまま Pen に渡すと Pen.Freeze が投げる）。
        // 今の値だけを写した複製を凍らせて使う。辞書の共有ブラシそのものには触らない。
        brush = brush.CloneCurrentValue();
        brush.Freeze();
        var pen = new Pen(brush, 2);
        pen.Freeze();
        return pen;
    }
}
