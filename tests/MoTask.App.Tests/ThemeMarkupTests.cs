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

    private static XDocument Load(string relativePath) => XDocument.Load(Path.Combine(XamlRoot, relativePath));

    private static IEnumerable<string> AllXaml()
        => Directory.EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "Industry.xaml");
}
