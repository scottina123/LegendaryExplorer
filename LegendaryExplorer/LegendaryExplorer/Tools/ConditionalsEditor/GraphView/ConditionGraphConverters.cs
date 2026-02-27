using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LegendaryExplorer.Tools.ConditionalsEditor.GraphView
{
    /// <summary>
    /// Returns the list of <see cref="PlotVarType"/> values for ComboBox binding.
    /// When used as a converter, converts nothing but returns the values list.
    /// </summary>
    public class PlotVarTypeListConverter : IValueConverter
    {
        private static readonly PlotVarType[] _values = Enum.GetValues<PlotVarType>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => _values;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Returns the list of <see cref="ComparisonOperator"/> values for ComboBox binding.
    /// </summary>
    public class ComparisonOperatorListConverter : IValueConverter
    {
        private static readonly ComparisonOperator[] _values = Enum.GetValues<ComparisonOperator>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => _values;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Returns the list of <see cref="LogicalOperator"/> values for ComboBox binding.
    /// </summary>
    public class LogicalOperatorListConverter : IValueConverter
    {
        private static readonly LogicalOperator[] _values = Enum.GetValues<LogicalOperator>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => _values;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Returns the list of <see cref="ArithmeticOperator"/> values for ComboBox binding.
    /// </summary>
    public class ArithmeticOperatorListConverter : IValueConverter
    {
        private static readonly ArithmeticOperator[] _values = Enum.GetValues<ArithmeticOperator>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => _values;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Converts a boolean to <see cref="Visibility"/>. True = Visible, False = Collapsed.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    /// <summary>
    /// Inverse of <see cref="BoolToVisibilityConverter"/>. True = Collapsed, False = Visible.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Collapsed;
    }
}
