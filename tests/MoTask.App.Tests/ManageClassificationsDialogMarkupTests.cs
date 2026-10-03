using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// 管理ダイアログの配線。ビューを実体化するには Application が要るので（BoardViewMarkupTests と同じ理由）、
/// マークアップをテキストとして読む。
/// </summary>
public class ManageClassificationsDialogMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// StaysOpen=False のポップアップは、外側のクリックで閉じた直後に、そのクリックを受けた ToggleButton が開き直す。
    /// 開いている間は色丸がクリックを受けないようにして、色丸のクリックで閉じられるようにする。
    /// </summary>
    [Fact]
    public void ColorDot_IgnoresClicksWhileThePopupIsOpen()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManageClassificationsDialog.xaml");
        var doc = XDocument.Load(path);
        var dot = doc.Descendants(Presentation + "ToggleButton").Single(e => (string?)e.Attribute(X + "Name") == "ColorDot");
        var popup = doc.Descendants(Presentation + "Popup").Single();

        var popupName = (string?)popup.Attribute(X + "Name");
        popupName.Should().NotBeNull();
        var trigger = dot.Descendants(Presentation + "DataTrigger").SingleOrDefault(t =>
            ((string?)t.Attribute("Binding") ?? "").Contains($"ElementName={popupName}")
            && ((string?)t.Attribute("Binding") ?? "").Contains("IsOpen"));
        trigger.Should().NotBeNull();
        trigger!.Descendants(Presentation + "Setter").Should().Contain(s =>
            (string?)s.Attribute("Property") == "IsHitTestVisible" && (string?)s.Attribute("Value") == "False");
    }
}
