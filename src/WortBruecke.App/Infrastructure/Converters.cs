using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WortBruecke.Core.Models;

namespace WortBruecke.App.Infrastructure;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class BooleanToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "Brush.Success" : "Brush.Error";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LocalizedText text ? text.For(parameter as string ?? "ru-RU") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class WindowWidthToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double actualWidth ? actualWidth : 0d;
        var text = parameter?.ToString() ?? "1060";
        var showBelow = text.StartsWith("<", StringComparison.Ordinal);
        if (!double.TryParse(text.TrimStart('<', '>'), NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold))
        {
            threshold = 1060d;
        }
        var visible = showBelow ? width < threshold : width >= threshold;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class WindowWidthToNavigationWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 112 DIP leaves room for the 24-DIP icon, focus ring and a vertical scrollbar at the
        // minimum supported window height; narrower rails clipped icons when scrolling appeared.
        return new GridLength(value is double width && width < 1060d ? 112d : 244d);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
