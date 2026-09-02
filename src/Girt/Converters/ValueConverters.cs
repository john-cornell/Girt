using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Girt.Models;

namespace Girt.Converters
{
    public class DepthToIndentConverter : IValueConverter
    {
        public double IndentPerLevel { get; set; } = 16;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var depth = value is int i ? i : 0;
            return new Thickness(depth * IndentPerLevel, 0, 0, 0);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // ConverterParameter="Empty" treats an int (e.g. a collection Count) as the
            // thing being checked, and shows when that count is zero - opposite polarity
            // to the default null-check mode, which shows when the value IS present.
            bool show;
            if (string.Equals(parameter as string, "Empty", StringComparison.OrdinalIgnoreCase) && value is int count)
            {
                show = count == 0;
            }
            else
            {
                var isNull = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
                show = !isNull;
            }

            if (Invert) show = !show;
            return show ? Visibility.Visible : Visibility.Collapsed;
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
            var invert = Invert || string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
            if (invert) flag = !flag;
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
            if (value is DiffLine line)
            {
                var app = Application.Current;

                // A Context line that still carries a CollapseGroupId was revealed by
                // "Expand Section" - tint it so it's obvious it can be re-collapsed.
                if (line.Type == DiffLineType.Context && line.CollapseGroupId.HasValue)
                {
                    return app.TryFindResource("DiffExpandedBgBrush") ?? Brushes.LightYellow;
                }

                return line.Type switch
                {
                    DiffLineType.Added => app.TryFindResource("DiffAddedBgBrush") ?? Brushes.LightGreen,
                    DiffLineType.Deleted => app.TryFindResource("DiffDeletedBgBrush") ?? Brushes.MistyRose,
                    DiffLineType.Header => app.TryFindResource("DiffHeaderBgBrush") ?? Brushes.LightBlue,
                    DiffLineType.CollapsedContext => app.TryFindResource("DiffHeaderBgBrush") ?? Brushes.LightBlue,
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
                    DiffLineType.CollapsedContext => app.TryFindResource("DiffHeaderTextBrush") ?? Brushes.DarkBlue,
                    _ => app.TryFindResource("TextPrimaryBrush") ?? Brushes.Black
                };
            }
            return Brushes.Black;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class DiffLineToggleLabelConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DiffLine line)
            {
                return line.Type == DiffLineType.CollapsedContext ? "▸ Expand Section" : "▾ Collapse Section";
            }
            return "Toggle Section";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class BranchPinToggleLabelConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var isPinned = value is bool b && b;
            return isPinned ? "📌 Unpin" : "📌 Pin";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class PushModeGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var opensReview = value is bool b && b;
            return opensReview ? "👁" : "⚡";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class PushModeTooltipConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var opensReview = value is bool b && b;
            return opensReview
                ? "Review mode: clicking 'to push' opens a diff review first. Click to switch to pushing immediately."
                : "Instant mode: clicking 'to push' pushes right away. Click to switch to reviewing first.";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
