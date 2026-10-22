using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;

namespace Nasaq.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value as string ?? "#888888");
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
