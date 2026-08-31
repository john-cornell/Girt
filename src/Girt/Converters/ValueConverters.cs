using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Girt.Models;

namespace Girt.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isNull = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
            if (Invert) isNull = !isNull;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (Invert) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    return new SolidColorBrush(color);
                }
                catch { }
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class DiffTypeToBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DiffLineType type)
            {
                var app = Application.Current;
                return type switch
                {
                    DiffLineType.Added => app.TryFindResource("DiffAddedBgBrush") ?? Brushes.LightGreen,
                    DiffLineType.Deleted => app.TryFindResource("DiffDeletedBgBrush") ?? Brushes.MistyRose,
                    DiffLineType.Header => app.TryFindResource("DiffHeaderBgBrush") ?? Brushes.LightBlue,
                    _ => Brushes.Transparent
                };
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class DiffTypeToForegroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DiffLineType type)
            {
                var app = Application.Current;
                return type switch
                {
                    DiffLineType.Added => app.TryFindResource("DiffAddedTextBrush") ?? Brushes.DarkGreen,
                    DiffLineType.Deleted => app.TryFindResource("DiffDeletedTextBrush") ?? Brushes.DarkRed,
                    DiffLineType.Header => app.TryFindResource("DiffHeaderTextBrush") ?? Brushes.DarkBlue,
                    _ => app.TryFindResource("TextPrimaryBrush") ?? Brushes.Black
                };
            }
            return Brushes.Black;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
