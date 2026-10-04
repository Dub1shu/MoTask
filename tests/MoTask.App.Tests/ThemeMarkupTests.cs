using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// テーマ（Fluent）の配線。ResourceDictionary を実体化するには Application が要るので
/// （BoardViewMarkupTests と同じ理由）、マークアップをテキストとして読む。
/// </summary>
public class ThemeMarkupTests
{
    private static readonly string XamlRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xaml");

    [Fact]
    public void App_FollowsTheSystemTheme()
    {
        var app = Load("App.xaml").Root!;

        ((string?)app.Attribute("ThemeMode")).Should().Be("System",
            "ライト／ダークとアクセント色を OS に追従させる土台");
    }

    [Fact]
    public void NoWindow_PaintsItsOwnBackground()
    {
        // Window に地の色を塗ると Mica が隠れる。
        foreach (var path in AllXaml())
        {
            File.ReadAllText(path).Should().NotContain("Brush.Bg", because: $"{Path.GetFileName(path)} が地を塗っている");
        }
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Fluent に任せると決めたコントロール。自前の暗黙スタイルがあると Fluent の見た目を上書きしてしまう。</summary>
    private static readonly string[] FluentOwnedTypes =
    {
        "Button", "TextBox", "ComboBox", "ComboBoxItem", "CheckBox", "DatePicker", "DatePickerTextBox",
        "Calendar", "ScrollBar", "MenuItem", "Separator", "Thumb",
    };

    [Fact]
    public void Controls_LeavesStandardControlsToFluent()
    {
        var implicitTypes = ImplicitStyles(Load(Path.Combine("Themes", "Controls.xaml")))
            .Select(s => (string?)s.Attribute("TargetType"))
            .ToList();

        implicitTypes.Should().NotContain(t => FluentOwnedTypes.Contains(t), "標準コントロールの見た目は Fluent に任せる");
        Load(Path.Combine("Themes", "Controls.xaml")).Descendants(Presentation + "ControlTemplate")
            .Select(t => (string?)t.Attribute("TargetType"))
            .Should().NotContain(new[] { "ScrollBar", "ContextMenu", "MenuItem", "ComboBox", "DatePicker", "TextBox" });
    }

    [Fact]
    public void Controls_PopupsInheritFluentAndOnlyResetTheFont()
    {
        // ContextMenu と ToolTip は別ウィンドウで開くので、Window の書体を引き継がない。書体だけ指定し直す。
        foreach (var type in new[] { "ContextMenu", "ToolTip" })
        {
            var style = ImplicitStyles(Load(Path.Combine("Themes", "Controls.xaml")))
                .SingleOrDefault(s => (string?)s.Attribute("TargetType") == type);

            style.Should().NotBeNull($"{type} の書体を Yu Gothic UI に戻すスタイルが要る");
            ((string?)style!.Attribute("BasedOn")).Should().Be($"{{StaticResource {{x:Type {type}}}}}");
            style.Elements(Presentation + "Setter").Select(s => (string?)s.Attribute("Property"))
                .Should().BeEquivalentTo(new[] { "FontFamily", "FontSize" });
        }
    }

    [Fact]
    public void PrimaryButton_IsFluentsAccentButton()
    {
        var primary = Load(Path.Combine("Themes", "Controls.xaml")).Descendants(Presentation + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "Btn.Primary");

        ((string?)primary.Attribute("BasedOn")).Should().Be("{StaticResource AccentButtonStyle}");
    }

    [Fact]
    public void NothingUsesTheRemovedFocusRing()
    {
        foreach (var path in AllXaml())
        {
            File.ReadAllText(path).Should().NotContain("Focus.Ring", because: $"{Path.GetFileName(path)} が消したスタイルを参照している");
        }
    }

    private static IEnumerable<XElement> ImplicitStyles(XDocument doc)
        => doc.Root!.Elements(Presentation + "Style").Where(s => s.Attribute(X + "Key") is null);

    private static XDocument Load(string relativePath) => XDocument.Load(Path.Combine(XamlRoot, relativePath));

    private static IEnumerable<string> AllXaml()
        => Directory.EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "Industry.xaml");
}
