using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MoTask.App.Converters;

/// <summary>null または空文字なら Collapsed。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value is string s && s.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
