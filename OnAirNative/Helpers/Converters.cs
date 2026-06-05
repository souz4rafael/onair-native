using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace OnAirNative.Helpers;

/// <summary>Returns Visible when a non-empty string is bound, Collapsed otherwise.</summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Returns Visible when bound value is true, Collapsed when false. Pass "invert" as parameter to flip.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = parameter?.ToString() == "invert";
        bool flag   = value is bool b && b;
        return (flag ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>Converts a double opacity (0.0–1.0) to a display percentage string.</summary>
public class OpacityToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d ? $"{(int)(d * 100)}%" : "75%";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
