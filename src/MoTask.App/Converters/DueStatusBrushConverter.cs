using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MoTask.Core.Filtering;

namespace MoTask.App.Converters;

/// <summary>期限超過は赤、当日はアクセント、それ以外は控えめな文字色。</summary>
public sealed class DueStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DueStatus.Overdue => "Brush.Danger",
            DueStatus.Today => "Brush.Accent",
            _ => "Brush.TextMuted",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? (object)DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
