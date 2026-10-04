using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MoTask.App.Behaviors;

/// <summary>
/// TextBox が空の間だけ透かし文字を重ねる添付ビヘイビア。
///
/// 以前は TextBox のテンプレートを上書きして Tag の文字列を出していたが、テンプレートを上書きすると
/// Fluent の入力欄（フォーカス時に下端がアクセント色になる）が失われる。WPF の Fluent の TextBox には
/// 透かし文字の機能が無いので、テンプレートには触らず Adorner で上に重ねる。
///
/// 「＋」で開く入力欄は最初 Collapsed なので、見えている間だけ Adorner を付ける。
/// 見えなくなっても付けたままだと、閉じた入力欄の位置に透かし文字だけが残る。
/// </summary>
public static class Watermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Watermark), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>空なら出す。空白 1 文字も入力として扱う（重ねると打った空白が見えなくなる）。</summary>
    internal static bool ShouldShow(string? text) => string.IsNullOrEmpty(text);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        // 付け直しで二重に購読しないよう、先に外してから付ける。
        box.Loaded -= OnBoxLoaded;
        box.TextChanged -= OnBoxTextChanged;
        box.IsVisibleChanged -= OnBoxIsVisibleChanged;
        box.Loaded += OnBoxLoaded;
        box.TextChanged += OnBoxTextChanged;
        box.IsVisibleChanged += OnBoxIsVisibleChanged;
        Update(box);
    }

    private static void OnBoxLoaded(object sender, RoutedEventArgs e) => Update((TextBox)sender);

    private static void OnBoxTextChanged(object sender, TextChangedEventArgs e) => Update((TextBox)sender);

    private static void OnBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((TextBox)sender);

    private static void Update(TextBox box)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer is null) return;

        var existing = layer.GetAdorners(box)?.OfType<WatermarkAdorner>().FirstOrDefault();
        var text = GetText(box);
        if (box.IsVisible && !string.IsNullOrEmpty(text) && ShouldShow(box.Text))
        {
            if (existing is null) layer.Add(new WatermarkAdorner(box, text));
            else existing.SetText(text);
        }
        else if (existing is not null)
        {
            layer.Remove(existing);
        }
    }
}
