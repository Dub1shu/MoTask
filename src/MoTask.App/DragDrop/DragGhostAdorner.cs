using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MoTask.App.DragDrop;

/// <summary>掴んでいる物の半透明の写し。カーソルに追従する。</summary>
public sealed class DragGhostAdorner : Adorner
{
    /// <summary>カーソルから写しの左上までのずらし幅。真上に置くと本文が隠れる。</summary>
    private const double Offset = 8;

    private readonly Brush _brush;
    private readonly Size _size;
    private Point? _position;

    /// <param name="adornedElement">ウィンドウ直下の要素。ここの AdornerLayer に載せる。</param>
    /// <param name="source">写す元。掴んでいるカードや列ヘッダー。</param>
    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        // 生きた Visual を写す VisualBrush は Freeze できないので、そのまま持つ。
        _brush = new VisualBrush(source) { Opacity = 0.65 };
        _size = new Size(source.ActualWidth, source.ActualHeight);
        IsHitTestVisible = false;
    }

    /// <param name="position">adornedElement から見たカーソル位置。</param>
    public void MoveTo(Point position)
    {
        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_position is not { } position) return;
        if (_size.Width <= 0 || _size.Height <= 0) return;

        drawingContext.DrawRectangle(_brush, null, new Rect(
            new Point(position.X + Offset, position.Y + Offset), _size));
    }
}
