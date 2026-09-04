using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MoTask.Core.Model;

namespace MoTask.App.Converters;

/// <summary>"accent-300" のようなランプ名を "Brush.Accent.300" リソースに解決する。</summary>
public sealed class RampBrushConverter : IValueConverter
{
    private const string FallbackKey = "Brush.Accent.300";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? Label.DefaultColor;
        var parts = name.Split('-');
        var key = parts.Length == 2 && parts[1].Length > 0
            ? $"Brush.{char.ToUpperInvariant(parts[0][0])}{parts[0][1..]}.{parts[1]}"
            : FallbackKey;
        return FindBrush(key) ?? FindBrush(FallbackKey) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush? FindBrush(string key) => Application.Current?.TryFindResource(key) as Brush;
}
