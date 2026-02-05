using System;
using System.Drawing;
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
        
        public DarkScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
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
                    // Toggle custom drawing based on dark mode
                    SetStyle(ControlStyles.UserPaint, value);
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
            set { _trackColor = value; Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the thumb color in dark mode.
        /// </summary>
        public Color ThumbColor
        {
            get => _thumbColor;
            set { _thumbColor = value; Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the thumb hover color in dark mode.
        /// </summary>
        public Color ThumbHoverColor
        {
            get => _thumbHoverColor;
            set { _thumbHoverColor = value; Invalidate(); }
        }
        
        /// <summary>
        /// Gets or sets the arrow button color in dark mode.
        /// </summary>
        public Color ArrowColor
        {
            get => _arrowColor;
            set { _arrowColor = value; Invalidate(); }
        }
        
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_isDarkMode)
            {
                base.OnPaint(e);
                return;
            }
            
            Graphics g = e.Graphics;
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
                    Color currentThumbColor = _thumbHovered ? _thumbHoverColor : _thumbColor;
                    using (var thumbBrush = new SolidBrush(currentThumbColor))
                    {
                        // Draw rounded thumb
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
            int thumbPosition = (int)((float)(Value - Minimum) / range * availableTrack);
            
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
                Invalidate();
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
                Invalidate();
            }
        }
    }
}
