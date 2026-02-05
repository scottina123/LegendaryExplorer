using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.SharedUI.Controls
{
    /// <summary>
    /// A TextBox with watermark/placeholder text that respects theme settings.
    /// </summary>
    public partial class WatermarkTextBox : UserControl
    {
        public WatermarkTextBox()
        {
            InitializeComponent();
        }

        #region Dependency Properties

        public static readonly DependencyProperty WatermarkProperty =
            DependencyProperty.Register(nameof(Watermark), typeof(string), typeof(WatermarkTextBox),
                new PropertyMetadata(string.Empty));

        public string Watermark
        {
            get => (string)GetValue(WatermarkProperty);
            set => SetValue(WatermarkProperty, value);
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(WatermarkTextBox),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty VerticalContentAlignmentProperty =
            DependencyProperty.Register(nameof(VerticalContentAlignment), typeof(VerticalAlignment), typeof(WatermarkTextBox),
                new PropertyMetadata(VerticalAlignment.Center));

        public new VerticalAlignment VerticalContentAlignment
        {
            get => (VerticalAlignment)GetValue(VerticalContentAlignmentProperty);
            set => SetValue(VerticalContentAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(WatermarkTextBox),
                new PropertyMetadata(false));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        #endregion

        #region Events

        public event TextChangedEventHandler TextChanged
        {
            add => PART_TextBox.TextChanged += value;
            remove => PART_TextBox.TextChanged -= value;
        }

        public new event KeyEventHandler KeyUp
        {
            add => PART_TextBox.KeyUp += value;
            remove => PART_TextBox.KeyUp -= value;
        }

        public new event KeyEventHandler KeyDown
        {
            add => PART_TextBox.KeyDown += value;
            remove => PART_TextBox.KeyDown -= value;
        }

        #endregion

        #region Methods

        public new void Focus()
        {
            PART_TextBox.Focus();
        }

        public void SelectAll()
        {
            PART_TextBox.SelectAll();
        }

        public void Clear()
        {
            PART_TextBox.Clear();
            Text = string.Empty;
        }

        #endregion
    }
}
