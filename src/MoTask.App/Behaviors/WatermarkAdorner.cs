using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MoTask.App.Behaviors;

/// <summary>
/// TextBox の入力開始位置に透かし文字を 1 行描く。クリックは下の TextBox に通す。
/// 色はテーマのキーを動的に引くので、OS のライト／ダーク切り替えに追従する。
/// </summary>
internal sealed class WatermarkAdorner : Adorner
{
    /// <summary>
    /// キャレットは Padding の内側さらに約 2px の位置に立つ。同じ位置に置くと先頭の字に重なるので、
    /// 透かし文字だけ右へずらす（旧テンプレートで実機合わせした値を引き継ぐ）。
    /// </summary>
    private const double CaretGap = 5;

    private readonly TextBlock _text;

    public WatermarkAdorner(TextBox box, string text) : base(box)
    {
        IsHitTestVisible = false;
        _text = new TextBlock { Text = text, FontFamily = box.FontFamily };
        // 入力した文字と見分けられるよう一段小さくする。
        _text.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Small");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        AddVisualChild(_text);
    }

    public void SetText(string text) => _text.Text = text;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _text;

    protected override Size MeasureOverride(Size constraint)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var box = (TextBox)AdornedElement;
        var left = box.BorderThickness.Left + box.Padding.Left + CaretGap;
        var top = (finalSize.Height - _text.DesiredSize.Height) / 2;
        _text.Arrange(new Rect(new Point(left, top), _text.DesiredSize));
        return finalSize;
    }
}
