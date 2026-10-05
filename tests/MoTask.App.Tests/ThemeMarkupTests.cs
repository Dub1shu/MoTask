using System.IO;
using System.Text.RegularExpressions;
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

    [Fact]
    public void DetailPanel_HintsUseTheWatermarkBehavior()
    {
        // TextBox のテンプレートは Fluent に任せたので、Tag に入れた文字はもう透かし文字にならない。
        var panel = File.ReadAllText(Path.Combine(XamlRoot, "Views", "TaskDetailPanel.xaml"));

        panel.Should().NotContain("Tag=\"{x:Static res:Strings.NewProjectHint}\"");
        panel.Should().NotContain("Tag=\"{x:Static res:Strings.NewLabelHint}\"");
        Regex.Matches(panel, @"behaviors:Watermark\.Text=""\{x:Static res:Strings\.New(Project|Label)Hint\}""")
            .Count.Should().Be(2);
    }

    [Fact]
    public void ThemeColors_AreLookedUpDynamically()
    {
        // StaticResource は一度しか評価されないので、OS のテーマを切り替えてもその部分だけ色が変わらない。
        // ラベルの段は RampBrushConverter 経由でしか引かないので、Industry.xaml の外に例外は無い。
        var offenders = AllXaml()
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"StaticResource\s+(Brush\.[A-Za-z0-9.]+)")
                .Select(m => $"{Path.GetFileName(path)}: {m.Groups[1].Value}"))
            .ToList();

        offenders.Should().BeEmpty();
    }

    /// <summary>Fluent のキーへ移して廃止した意味色。ラベルの段（Accent / Neutral などの数字付き）は残す。</summary>
    private static readonly string[] RetiredSemanticColors =
    {
        "Bg", "Surface", "SurfaceRaised", "SurfaceHover", "Input", "Text", "TextMuted",
        "Accent", "AccentSubtle", "Divider", "Danger", "OnAccent",
    };

    [Fact]
    public void Industry_NoLongerDefinesSemanticColors()
    {
        var keys = Load(Path.Combine("Themes", "Industry.xaml")).Descendants()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)
            .ToHashSet();

        foreach (var name in RetiredSemanticColors)
        {
            keys.Should().NotContain($"Brush.{name}", "テーマの色は Fluent のキーで引く");
            keys.Should().NotContain($"Color.{name}");
        }
        keys.Should().Contain(new[] { "Brush.Neutral.100", "Brush.Neutral.900", "Brush.Accent.300" },
            "ラベルの段は文字色と既定色に使うので残す");
    }

    [Fact]
    public void DueDateColor_FollowsTheTheme()
    {
        // コンバーターで一度だけ引いた色は、OS のテーマを切り替えても変わらない。
        foreach (var path in AllXaml())
        {
            File.ReadAllText(path).Should().NotContain("DueStatusBrush", because: Path.GetFileName(path));
        }
    }

    [Fact]
    public void DeletedCard_DashedOutlineUsesTheStrongStroke()
    {
        // 削除済みカードは枠を 0 にするので、点線がカードの唯一の輪郭になる。
        // ControlStrokeColorDefaultBrush は 6〜7% の不透明度しかなく、どちらのテーマでもほぼ見えない。
        var dashed = Load(Path.Combine("Views", "ColumnView.xaml")).Descendants(Presentation + "Rectangle")
            .Single(r => (string?)r.Attribute(X + "Name") == "Dashed");

        ((string?)dashed.Attribute("Stroke")).Should().Be("{DynamicResource ControlStrongStrokeColorDefaultBrush}");
    }

    [Fact]
    public void ArchiveRow_KeepsTheSelectedBorderWhileHovered()
    {
        // Fluent の Button はホバー時の枠色をテンプレートのトリガで当てる。テンプレートのトリガは派生スタイルの
        // DataTrigger より強いので、Fluent の Button を土台にすると選択中の行がホバー中だけアクセントの枠を失う。
        // 行は自前のテンプレートを持ち、選択中の枠をスタイルのトリガで当てる。
        var row = Load(Path.Combine("Views", "ArchiveView.xaml")).Descendants(Presentation + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "Archive.Row");

        row.Attribute("BasedOn").Should().BeNull("Fluent の Button のテンプレートを持ち込まない");
        row.Elements(Presentation + "Setter").Select(s => (string?)s.Attribute("Property")).Should().Contain("Template");
        var selected = row.Descendants(Presentation + "DataTrigger")
            .Single(t => (string?)t.Attribute("Binding") == "{Binding IsSelected}");
        selected.Elements(Presentation + "Setter")
            .Should().Contain(s => (string?)s.Attribute("Property") == "BorderBrush"
                                   && (string?)s.Attribute("Value") == "{DynamicResource AccentFillColorDefaultBrush}");
    }

    private static IEnumerable<XElement> ImplicitStyles(XDocument doc)
        => doc.Root!.Elements(Presentation + "Style").Where(s => s.Attribute(X + "Key") is null);

    private static XDocument Load(string relativePath) => XDocument.Load(Path.Combine(XamlRoot, relativePath));

    private static IEnumerable<string> AllXaml()
        => Directory.EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "Industry.xaml");
}
