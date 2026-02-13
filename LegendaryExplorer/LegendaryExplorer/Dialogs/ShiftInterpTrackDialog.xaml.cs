using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs
{
    public class ShiftInterpTrackParameters
    {
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
        public float TimeOffset { get; set; }

        public ShiftInterpTrackParameters()
        {
            OffsetX = 0;
            OffsetY = 0;
            OffsetZ = 0;
            Roll = 0;
            Pitch = 0;
            Yaw = 0;
            TimeOffset = 0;
        }
    }

    public partial class ShiftInterpTrackDialog : Window
    {
        public ShiftInterpTrackParameters Parameters { get; private set; }

        public ShiftInterpTrackDialog()
        {
            Parameters = new ShiftInterpTrackParameters();
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!float.TryParse(OffsetXTextBox.Text, out var offsetX) ||
                !float.TryParse(OffsetYTextBox.Text, out var offsetY) ||
                !float.TryParse(OffsetZTextBox.Text, out var offsetZ) ||
                !float.TryParse(RollTextBox.Text, out var roll) ||
                !float.TryParse(PitchTextBox.Text, out var pitch) ||
                !float.TryParse(YawTextBox.Text, out var yaw) ||
                !float.TryParse(TimeOffsetTextBox.Text, out var timeOffset))
            {
                MessageBox.Show("All fields must contain valid numbers.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Parameters.OffsetX = offsetX;
            Parameters.OffsetY = offsetY;
            Parameters.OffsetZ = offsetZ;
            Parameters.Roll = roll;
            Parameters.Pitch = pitch;
            Parameters.Yaw = yaw;
            Parameters.TimeOffset = timeOffset;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
