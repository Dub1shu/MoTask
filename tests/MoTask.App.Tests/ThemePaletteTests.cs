using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// 見本の色が、すべてテーマの Brush に解決できること。ResourceDictionary を実体化するには Application が要るので
/// （BoardViewMarkupTests と同じ理由）、マークアップをテキストとして読む。
/// </summary>
public class ThemePaletteTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryPaletteColor_HasABrushInTheTheme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Industry.xaml");
        var keys = XDocument.Load(path).Descendants()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)
            .ToHashSet();

        foreach (var color in LabelPalette.Colors)
        {
            var parts = color.Split('-');
            var key = $"Brush.{char.ToUpperInvariant(parts[0][0])}{parts[0][1..]}.{parts[1]}";
            keys.Should().Contain(key, because: $"{color} を見本に出している");
        }
    }
}
