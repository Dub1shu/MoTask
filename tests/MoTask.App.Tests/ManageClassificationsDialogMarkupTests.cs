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

    private static XDocument Dialog()
        => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManageClassificationsDialog.xaml"));

    private static readonly XNamespace Dd = "clr-namespace:MoTask.App.DragDrop";

    /// <summary>2 つの一覧はどちらもドラッグ元でありドロップ先。ハンドラはコードビハインドで挿す。</summary>
    [Theory]
    [InlineData("ProjectList")]
    [InlineData("LabelList")]
    public void Lists_AreDragSourcesAndDropTargets(string name)
    {
        var list = Dialog().Descendants(Presentation + "ItemsControl").Single(e => (string?)e.Attribute(X + "Name") == name);

        ((string?)list.Attribute(Dd + "DragDropBehavior.IsDragSource")).Should().Be("True");
        ((string?)list.Attribute(Dd + "DragDropBehavior.IsDropTarget")).Should().Be("True");
    }

    /// <summary>アーカイブ済みと名前の編集中の行は掴めない。</summary>
    [Theory]
    [InlineData("IsArchived")]
    [InlineData("IsEditing")]
    public void Row_IgnoresDragWhile(string property)
    {
        var row = Dialog().Descendants(Presentation + "DataTemplate").Single(e => (string?)e.Attribute(X + "Key") == "Manage.Row");
        var trigger = row.Descendants(Presentation + "DataTrigger").Where(t => (string?)t.Attribute("Binding") == $"{{Binding {property}}}");

        trigger.Should().Contain(t => t.Descendants(Presentation + "Setter").Any(s =>
            (string?)s.Attribute("Property") == "dd:DragDropBehavior.DragSourceIgnore" && (string?)s.Attribute("Value") == "True"));
    }
}
