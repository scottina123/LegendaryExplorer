using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Be.Windows.Forms
{
    /// <summary>
    /// A custom-drawn VScrollBar that supports dark mode theming.
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
        
        // Windows messages
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_PRINTCLIENT = 0x0318;
        private const int WM_PRINT = 0x0317;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_CAPTURECHANGED = 0x0215;
        
        // Timer for continuous repaint during drag
        private Timer _repaintTimer;
        
        // For double buffering
        private Bitmap _backBuffer;
        
        public DarkScrollBar()
        {
            // Don't use UserPaint style - it breaks scrollbar functionality
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            
            // Setup repaint timer for smooth updates during drag
            _repaintTimer = new Timer();
            _repaintTimer.Interval = 16; // ~60fps
            _repaintTimer.Tick += (s, e) => 
            {
                if (_isDarkMode && _isDragging)
                {
                    PaintDarkScrollBar();
                }
            };
        }
        
        /// <summary>
        /// Gets or sets whether dark mode is enabled for this scrollbar.
        /// </summary>
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
        public Color TrackColor
        {
            get => _trackColor;
            set { _trackColor = value; if (_isDarkMode) Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the thumb color in dark mode.
        /// </summary>
        public Color ThumbColor
        {
            get => _thumbColor;
            set { _thumbColor = value; if (_isDarkMode) Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the thumb hover color in dark mode.
        /// </summary>
        public Color ThumbHoverColor
        {
            get => _thumbHoverColor;
            set { _thumbHoverColor = value; if (_isDarkMode) Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the arrow button color in dark mode.
        /// </summary>
        public Color ArrowColor
        {
            get => _arrowColor;
            set { _arrowColor = value; if (_isDarkMode) Invalidate(); }
        }
        
        /// <summary>
        /// Override WndProc to intercept paint messages and handle them ourselves in dark mode.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (_isDarkMode)
            {
                switch (m.Msg)
                {
                    case WM_ERASEBKGND:
                        // Prevent background erase flicker
                        m.Result = (IntPtr)1;
                        return;
                    
                    case WM_NCPAINT:
                        // Handle non-client paint
                        m.Result = IntPtr.Zero;
                        return;
                        
                    case WM_PAINT:
                        // Let base handle the scroll logic first
                        base.WndProc(ref m);
                        // Then immediately paint over with our dark theme
                        PaintDarkScrollBar();
                        return;
                    
                    case WM_PRINTCLIENT:
                    case WM_PRINT:
                        // Handle print messages for proper rendering
                        PaintDarkScrollBar();
                        m.Result = IntPtr.Zero;
                        return;
                        
                    case WM_LBUTTONDOWN:
                        _isDragging = true;
                        _repaintTimer.Start();
                        base.WndProc(ref m);
                        PaintDarkScrollBar();
                        return;
                        
                    case WM_LBUTTONUP:
                        _isDragging = false;
                        _repaintTimer.Stop();
                        base.WndProc(ref m);
                        PaintDarkScrollBar();
                        return;
                        
                    case WM_CAPTURECHANGED:
                        _isDragging = false;
                        _repaintTimer.Stop();
                        base.WndProc(ref m);
                        PaintDarkScrollBar();
                        return;
                        
                    case WM_MOUSEMOVE:
                        base.WndProc(ref m);
                        if (_isDragging)
                        {
                            PaintDarkScrollBar();
                        }
                        return;
                }
            }
            
            base.WndProc(ref m);
        }
        
        [DllImport("user32.dll")]
        private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);
        
        /// <summary>
        /// Paints the scrollbar with dark mode colors using double buffering.
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
                    Rectangle rect = ClientRectangle;
                    
                    // Draw track background
                    using (var trackBrush = new SolidBrush(_trackColor))
                    {
                        bufferGraphics.FillRectangle(trackBrush, rect);
                    }
                    
                    // Draw border
                    using (var borderPen = new Pen(_borderColor))
                    {
                        bufferGraphics.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);
                    }
                    
                    int arrowHeight = SystemInformation.VerticalScrollBarArrowHeight;
                    
                    // Draw up arrow button
                    Rectangle upArrowRect = new Rectangle(0, 0, rect.Width, arrowHeight);
                    DrawArrowButton(bufferGraphics, upArrowRect, true, _upArrowHovered);
                    
                    // Draw down arrow button  
                    Rectangle downArrowRect = new Rectangle(0, rect.Height - arrowHeight, rect.Width, arrowHeight);
                    DrawArrowButton(bufferGraphics, downArrowRect, false, _downArrowHovered);
                    
                    // Calculate and draw thumb
                    if (Maximum > Minimum)
                    {
                        Rectangle thumbRect = GetThumbRect();
                        if (thumbRect.Height > 0)
                        {
                            // Use hover color when dragging or hovering
                            Color currentThumbColor = (_thumbHovered || _isDragging) ? _thumbHoverColor : _thumbColor;
                            using (var thumbBrush = new SolidBrush(currentThumbColor))
                            {
                                // Draw thumb with padding
                                int padding = 2;
                                Rectangle innerThumb = new Rectangle(
                                    thumbRect.X + padding,
                                    thumbRect.Y + padding,
                                    thumbRect.Width - (padding * 2),
                                    thumbRect.Height - (padding * 2));
                                
                                if (innerThumb.Width > 0 && innerThumb.Height > 0)
                                {
                                    bufferGraphics.FillRectangle(thumbBrush, innerThumb);
                                }
                            }
                        }
                    }
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
                _repaintTimer?.Stop();
                _repaintTimer?.Dispose();
                _repaintTimer = null;
                _backBuffer?.Dispose();
                _backBuffer = null;
            }
            base.Dispose(disposing);
        }
    }
}
