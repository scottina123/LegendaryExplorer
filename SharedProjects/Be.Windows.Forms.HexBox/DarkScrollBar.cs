using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Be.Windows.Forms
{
    /// <summary>
    /// A custom-drawn VScrollBar that supports dark mode theming.
    /// Intercepts native paint messages via WndProc to completely bypass
    /// the native scrollbar rendering, preventing light/dark flicker.
    /// Thumb dragging is handled entirely in managed code to avoid
    /// the native modal tracking loop which paints directly via GetDC.
    /// </summary>
    public class DarkScrollBar : VScrollBar
    {
        private bool _isDarkMode;
        private Color _trackColor = Color.FromArgb(0x3E, 0x3E, 0x42);      // Dark track
        private Color _thumbColor = Color.FromArgb(0x68, 0x68, 0x6B);      // Dark thumb
        private Color _thumbHoverColor = Color.FromArgb(0x9E, 0x9E, 0x9E); // Lighter on hover
        private Color _arrowColor = Color.FromArgb(0x99, 0x99, 0x99);      // Arrow color
        private Color _borderColor = Color.FromArgb(0x3F, 0x3F, 0x46);     // Border

        private bool _thumbHovered;
        private bool _upArrowHovered;
        private bool _downArrowHovered;
        private bool _isDragging;

        // Custom thumb drag state
        private bool _isThumbDragging;
        private int _thumbDragStartMouseY;
        private int _thumbDragStartValue;

        // Windows messages
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_TIMER = 0x0113;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_PRINTCLIENT = 0x0318;
        private const int WM_PRINT = 0x0317;

        // Scrollbar-specific messages (sent when Value/Range change)
        private const int SBM_SETPOS = 0x00E0;
        private const int SBM_SETRANGE = 0x00E2;
        private const int SBM_SETRANGEREDRAW = 0x00E6;
        private const int SBM_SETSCROLLINFO = 0x00E9;

        // For double buffering
        private Bitmap _backBuffer;

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public DarkScrollBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        /// <summary>
        /// Gets or sets whether dark mode is enabled for this scrollbar.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (_isDarkMode != value)
                {
                    _isDarkMode = value;
                    Invalidate();
                }
            }
        }

        /// <summary>
        /// Gets or sets the track (background) color in dark mode.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color TrackColor
        {
            get => _trackColor;
            set { _trackColor = value; if (_isDarkMode) Invalidate(); }
        }

        /// <summary>
        /// Gets or sets the thumb color in dark mode.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color ThumbColor
        {
            get => _thumbColor;
            set { _thumbColor = value; if (_isDarkMode) Invalidate(); }
        }

        /// <summary>
        /// Gets or sets the thumb hover color in dark mode.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color ThumbHoverColor
        {
            get => _thumbHoverColor;
            set { _thumbHoverColor = value; if (_isDarkMode) Invalidate(); }
        }

        /// <summary>
        /// Gets or sets the arrow button color in dark mode.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color ArrowColor
        {
            get => _arrowColor;
            set { _arrowColor = value; if (_isDarkMode) Invalidate(); }
        }

        protected override void WndProc(ref Message m)
        {
            if (_isDarkMode)
            {
                switch (m.Msg)
                {
                    case WM_ERASEBKGND:
                        m.Result = (IntPtr)1;
                        return;

                    case WM_NCPAINT:
                        m.Result = IntPtr.Zero;
                        return;

                    case WM_PAINT:
                        // Handle WM_PAINT entirely ourselves via BeginPaint/EndPaint.
                        // Never call base so the native scrollbar cannot render.
                        IntPtr hdc = BeginPaint(Handle, out PAINTSTRUCT ps);
                        try
                        {
                            using (var g = Graphics.FromHdc(hdc))
                            {
                                PaintDarkContent(g);
                            }
                        }
                        finally
                        {
                            EndPaint(Handle, ref ps);
                        }
                        return;

                    case WM_PRINTCLIENT:
                    case WM_PRINT:
                        PaintDarkScrollBar();
                        m.Result = IntPtr.Zero;
                        return;

                    case WM_LBUTTONDOWN:
                        HandleDarkMouseDown(ref m);
                        return;

                    case WM_LBUTTONUP:
                        HandleDarkMouseUp(ref m);
                        return;

                    case WM_CAPTURECHANGED:
                        if (_isThumbDragging)
                        {
                            // Thumb drag was interrupted (e.g., focus stolen)
                            _isThumbDragging = false;
                            _isDragging = false;
                            PaintDarkScrollBar();
                        }
                        else
                        {
                            _isDragging = false;
                            base.WndProc(ref m);
                            PaintDarkScrollBar();
                        }
                        return;

                    case WM_MOUSEMOVE:
                        HandleDarkMouseMove(ref m);
                        return;

                    // Timer fires for repeat-scroll when holding an arrow button
                    case WM_TIMER:
                        BaseWndProcWithoutRedraw(ref m);
                        return;

                    // Scrollbar-specific messages sent when Value/Range change from code.
                    // The native control repaints directly when processing these.
                    case SBM_SETPOS:
                    case SBM_SETRANGE:
                    case SBM_SETRANGEREDRAW:
                    case SBM_SETSCROLLINFO:
                        BaseWndProcWithoutRedraw(ref m);
                        return;
                }
            }

            base.WndProc(ref m);
        }

        private static int GetYFromLParam(IntPtr lParam)
        {
            return (short)((int)lParam >> 16);
        }

        /// <summary>
        /// Handles WM_LBUTTONDOWN in dark mode.
        /// If the click is on the thumb, starts a custom managed drag loop
        /// to avoid the native modal tracking loop (which paints via GetDC).
        /// For clicks on arrows or the track, forwards to base with rendering suppressed.
        /// </summary>
        private void HandleDarkMouseDown(ref Message m)
        {
            int mouseY = GetYFromLParam(m.LParam);
            Rectangle thumbRect = GetThumbRect();

            if (thumbRect.Height > 0 && thumbRect.Contains(new Point(Width / 2, mouseY)))
            {
                // Thumb click: start our own drag. Do NOT call base.WndProc
                // because it enters a native modal tracking loop that paints
                // the light scrollbar directly via GetDC.
                _isThumbDragging = true;
                _isDragging = true;
                _thumbDragStartMouseY = mouseY;
                _thumbDragStartValue = Value;
                Capture = true;
                PaintDarkScrollBar();

                // Notify listeners that scrolling has started
                OnScroll(new ScrollEventArgs(ScrollEventType.ThumbTrack, Value));
            }
            else
            {
                // Arrow or track click: let base handle with rendering suppressed.
                // These don't use a modal loop for the initial click.
                _isDragging = true;
                BaseWndProcWithoutRedraw(ref m);
            }
        }

        /// <summary>
        /// Handles WM_MOUSEMOVE in dark mode.
        /// During a custom thumb drag, calculates the new Value from mouse position.
        /// </summary>
        private void HandleDarkMouseMove(ref Message m)
        {
            if (_isThumbDragging)
            {
                int mouseY = GetYFromLParam(m.LParam);
                int deltaY = mouseY - _thumbDragStartMouseY;

                int arrowHeight = SystemInformation.VerticalScrollBarArrowHeight;
                int trackHeight = Height - (arrowHeight * 2);
                int range = Maximum - Minimum;

                if (range <= 0 || trackHeight <= 0)
                    return;

                int thumbHeight = Math.Max(20, (int)((float)LargeChange / (range + LargeChange) * trackHeight));
                int availableTrack = trackHeight - thumbHeight;

                if (availableTrack > 0)
                {
                    int newValue = _thumbDragStartValue + (int)((float)deltaY / availableTrack * range);
                    newValue = Math.Max(Minimum, Math.Min(Maximum - LargeChange + 1, newValue));

                    if (newValue != Value)
                    {
                        Value = newValue;
                        OnScroll(new ScrollEventArgs(ScrollEventType.ThumbTrack, newValue));
                    }
                }

                PaintDarkScrollBar();
            }
            else
            {
                base.WndProc(ref m);
            }
        }

        /// <summary>
        /// Handles WM_LBUTTONUP in dark mode.
        /// Ends custom thumb drag or forwards to base for arrow/track clicks.
        /// </summary>
        private void HandleDarkMouseUp(ref Message m)
        {
            if (_isThumbDragging)
            {
                _isThumbDragging = false;
                _isDragging = false;
                Capture = false;

                OnScroll(new ScrollEventArgs(ScrollEventType.ThumbPosition, Value));
                OnScroll(new ScrollEventArgs(ScrollEventType.EndScroll, Value));
                PaintDarkScrollBar();
            }
            else
            {
                _isDragging = false;
                BaseWndProcWithoutRedraw(ref m);
            }
        }

        /// <summary>
        /// Forwards a message to the base WndProc with native rendering suppressed.
        /// WM_SETREDRAW(FALSE) prevents the native scrollbar from painting to screen
        /// during its internal processing, then we re-enable drawing and paint our
        /// dark version immediately.
        /// </summary>
        private void BaseWndProcWithoutRedraw(ref Message m)
        {
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            base.WndProc(ref m);
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            PaintDarkScrollBar();
        }

        /// <summary>
        /// Paints dark scrollbar content directly to the provided Graphics.
        /// </summary>
        private void PaintDarkContent(Graphics g)
        {
            if (Width <= 0 || Height <= 0)
                return;

            Rectangle rect = ClientRectangle;

            // Draw track background
            using (var trackBrush = new SolidBrush(_trackColor))
            {
                g.FillRectangle(trackBrush, rect);
            }

            // Draw border
            using (var borderPen = new Pen(_borderColor))
            {
                g.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);
            }

            int arrowHeight = SystemInformation.VerticalScrollBarArrowHeight;

            // Draw up arrow button
            Rectangle upArrowRect = new Rectangle(0, 0, rect.Width, arrowHeight);
            DrawArrowButton(g, upArrowRect, true, _upArrowHovered);

            // Draw down arrow button
            Rectangle downArrowRect = new Rectangle(0, rect.Height - arrowHeight, rect.Width, arrowHeight);
            DrawArrowButton(g, downArrowRect, false, _downArrowHovered);

            // Calculate and draw thumb
            if (Maximum > Minimum)
            {
                Rectangle thumbRect = GetThumbRect();
                if (thumbRect.Height > 0)
                {
                    Color currentThumbColor = (_thumbHovered || _isDragging) ? _thumbHoverColor : _thumbColor;
                    using (var thumbBrush = new SolidBrush(currentThumbColor))
                    {
                        int padding = 2;
                        Rectangle innerThumb = new Rectangle(
                            thumbRect.X + padding,
                            thumbRect.Y + padding,
                            thumbRect.Width - (padding * 2),
                            thumbRect.Height - (padding * 2));

                        if (innerThumb.Width > 0 && innerThumb.Height > 0)
                        {
                            g.FillRectangle(thumbBrush, innerThumb);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Paints the scrollbar with dark mode colors using double buffering.
        /// Used for repaints outside of WM_PAINT (e.g., after mouse interaction).
        /// </summary>
        private void PaintDarkScrollBar()
        {
            if (Width <= 0 || Height <= 0 || !IsHandleCreated || IsDisposed)
                return;

            try
            {
                // Create or resize back buffer
                if (_backBuffer == null || _backBuffer.Width != Width || _backBuffer.Height != Height)
                {
                    _backBuffer?.Dispose();
                    _backBuffer = new Bitmap(Width, Height);
                }

                // Draw to back buffer
                using (Graphics bufferGraphics = Graphics.FromImage(_backBuffer))
                {
                    PaintDarkContent(bufferGraphics);
                }

                // Copy back buffer to screen
                using (Graphics screenGraphics = Graphics.FromHwnd(Handle))
                {
                    screenGraphics.DrawImageUnscaled(_backBuffer, 0, 0);
                }
            }
            catch
            {
                // Ignore paint errors (control might be disposing)
            }
        }

        private void DrawArrowButton(Graphics g, Rectangle rect, bool isUp, bool isHovered)
        {
            // Draw button background if hovered
            if (isHovered)
            {
                using (var hoverBrush = new SolidBrush(Color.FromArgb(0x50, 0x50, 0x54)))
                {
                    g.FillRectangle(hoverBrush, rect);
                }
            }

            // Draw arrow
            int arrowSize = 5;
            int centerX = rect.X + rect.Width / 2;
            int centerY = rect.Y + rect.Height / 2;

            Point[] arrowPoints;
            if (isUp)
            {
                arrowPoints = new Point[]
                {
                    new Point(centerX, centerY - arrowSize / 2),
                    new Point(centerX - arrowSize, centerY + arrowSize / 2),
                    new Point(centerX + arrowSize, centerY + arrowSize / 2)
                };
            }
            else
            {
                arrowPoints = new Point[]
                {
                    new Point(centerX, centerY + arrowSize / 2),
                    new Point(centerX - arrowSize, centerY - arrowSize / 2),
                    new Point(centerX + arrowSize, centerY - arrowSize / 2)
                };
            }

            using (var arrowBrush = new SolidBrush(_arrowColor))
            {
                g.FillPolygon(arrowBrush, arrowPoints);
            }
        }

        private Rectangle GetThumbRect()
        {
            int arrowHeight = SystemInformation.VerticalScrollBarArrowHeight;
            int trackHeight = Height - (arrowHeight * 2);

            if (trackHeight <= 0 || Maximum <= Minimum)
                return Rectangle.Empty;

            int range = Maximum - Minimum;
            int thumbHeight = Math.Max(20, (int)((float)LargeChange / (range + LargeChange) * trackHeight));

            int availableTrack = trackHeight - thumbHeight;
            int thumbPosition = range > 0 ? (int)((float)(Value - Minimum) / range * availableTrack) : 0;

            return new Rectangle(0, arrowHeight + thumbPosition, Width, thumbHeight);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_isDarkMode) return;

            int arrowHeight = SystemInformation.VerticalScrollBarArrowHeight;
            Rectangle thumbRect = GetThumbRect();

            bool wasThumbHovered = _thumbHovered;
            bool wasUpHovered = _upArrowHovered;
            bool wasDownHovered = _downArrowHovered;

            _thumbHovered = thumbRect.Contains(e.Location);
            _upArrowHovered = e.Y < arrowHeight;
            _downArrowHovered = e.Y > Height - arrowHeight;

            if (wasThumbHovered != _thumbHovered || wasUpHovered != _upArrowHovered || wasDownHovered != _downArrowHovered)
            {
                PaintDarkScrollBar();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_isDarkMode)
            {
                _thumbHovered = false;
                _upArrowHovered = false;
                _downArrowHovered = false;
                PaintDarkScrollBar();
            }
        }

        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);

            if (_isDarkMode)
            {
                PaintDarkScrollBar();
            }
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);

            if (_isDarkMode)
            {
                PaintDarkScrollBar();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _backBuffer?.Dispose();
                _backBuffer = null;
            }
            base.Dispose(disposing);
        }
    }
}
