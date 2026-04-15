using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeSessionManager.Converters;

/// <summary>空文字列 → Collapsed、非空文字列 → Visible</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
