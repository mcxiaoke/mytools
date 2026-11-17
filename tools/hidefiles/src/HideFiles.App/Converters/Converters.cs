using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HideFiles.Core;

namespace HideFiles.App.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FileStatus status)
        {
            return status switch
            {
                FileStatus.Obfuscated => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // Amber/Yellow
                FileStatus.Normal => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),     // Green
                FileStatus.Corrupted => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),  // Red
                FileStatus.Locked => new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)),     // Indigo
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            bool isInvert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
            bool visible = isInvert ? !b : b;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ListToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable<string> list)
        {
            return string.Join(", ", list);
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
