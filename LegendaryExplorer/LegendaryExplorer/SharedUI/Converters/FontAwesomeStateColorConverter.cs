using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LegendaryExplorer.SharedUI.Converters
{
    /// <summary>
    /// Returns a theme-aware foreground for a FontAwesome state icon.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class FontAwesomeStateColorConverter : IValueConverter
    {
        // parameter is allowed class type for visibility
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string resourceKey = (bool)value ? "IconForegroundBrush" : "MutedTextBrush";
            return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
