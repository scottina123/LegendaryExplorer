using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LegendaryExplorer.SharedUI.Converters
{
    /// <summary>
    /// Returns a color for setting to the foreground of a button for Save Hex Changes
    /// </summary>
    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class UnsavedChangesForegroundConverter : IValueConverter
    {
        // parameter is allowed class type for visibility
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if ((bool)value) { return new SolidColorBrush(Colors.Red); }
            // Use system color for normal state to respect dark/light themes
            return SystemColors.ControlTextBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
