using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;

namespace Be.Windows.Forms
{
    /// <summary>
    /// Defines a build-in ContextMenuStrip manager for HexBox control to show Copy, Cut, Paste menu in contextmenu of the control.
    /// </summary>
    [TypeConverterAttribute(typeof(ExpandableObjectConverter))]
    public sealed class BuiltInContextMenu : Component
    {
        /// <summary>
        /// Contains the HexBox control.
        /// </summary>
        HexBox _hexBox;
        /// <summary>
        /// Contains the ContextMenuStrip control.
        /// </summary>
        ContextMenuStrip _contextMenuStrip;
        /// <summary>
        /// Contains the "Cut"-ToolStripMenuItem object.
        /// </summary>
        ToolStripMenuItem _cutToolStripMenuItem;
        /// <summary>
        /// Contains the "Copy"-ToolStripMenuItem object.
        /// </summary>
        ToolStripMenuItem _copyToolStripMenuItem;
        /// <summary>
        /// Contains the "Paste"-ToolStripMenuItem object.
        /// </summary>
        ToolStripMenuItem _pasteToolStripMenuItem;
        /// <summary>
        /// Contains the "Select All"-ToolStripMenuItem object.
        /// </summary>
        ToolStripMenuItem _selectAllToolStripMenuItem;

        bool _isDarkMode;

        /// <summary>
        /// Gets or sets whether dark mode is enabled for the context menu.
        /// </summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (_isDarkMode != value)
                {
                    _isDarkMode = value;
                    ApplyDarkMode();
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of BuildInContextMenu class.
        /// </summary>
        /// <param name="hexBox">the HexBox control</param>
        internal BuiltInContextMenu(HexBox hexBox)
        {
            _hexBox = hexBox;
            _hexBox.ByteProviderChanged += new EventHandler(HexBox_ByteProviderChanged);
        }
        /// <summary>
        /// If ByteProvider
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void HexBox_ByteProviderChanged(object sender, EventArgs e)
        {
            CheckBuiltInContextMenu();
        }
        /// <summary>
        /// Assigns the ContextMenuStrip control to the HexBox control.
        /// </summary>
        void CheckBuiltInContextMenu()
        {
            if (Util.DesignMode)
                return;

            if (this._contextMenuStrip == null)
            {
                ContextMenuStrip cms = new ContextMenuStrip();
                _cutToolStripMenuItem = new ToolStripMenuItem(CutMenuItemTextInternal, CutMenuItemImage, new EventHandler(CutMenuItem_Click));
                cms.Items.Add(_cutToolStripMenuItem);
                _copyToolStripMenuItem = new ToolStripMenuItem(CopyMenuItemTextInternal, CopyMenuItemImage, new EventHandler(CopyMenuItem_Click));
                cms.Items.Add(_copyToolStripMenuItem);
                _pasteToolStripMenuItem = new ToolStripMenuItem(PasteMenuItemTextInternal, PasteMenuItemImage, new EventHandler(PasteMenuItem_Click));
                cms.Items.Add(_pasteToolStripMenuItem);

                cms.Items.Add(new ToolStripSeparator());

                _selectAllToolStripMenuItem = new ToolStripMenuItem(SelectAllMenuItemTextInternal, SelectAllMenuItemImage, new EventHandler(SelectAllMenuItem_Click));
                cms.Items.Add(_selectAllToolStripMenuItem);
                cms.Opening += new CancelEventHandler(BuildInContextMenuStrip_Opening);

                _contextMenuStrip = cms;
                ApplyDarkMode();
            }

            if (this._hexBox.ByteProvider == null && this._hexBox.ContextMenuStrip == this._contextMenuStrip)
                this._hexBox.ContextMenuStrip = null;
            else if (this._hexBox.ByteProvider != null && this._hexBox.ContextMenuStrip == null)
                this._hexBox.ContextMenuStrip = _contextMenuStrip;
        }
        /// <summary>
        /// Before opening the ContextMenuStrip, we manage the availability of the items.
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void BuildInContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            _cutToolStripMenuItem.Enabled = this._hexBox.CanCut();
            _copyToolStripMenuItem.Enabled = this._hexBox.CanCopy();
            _pasteToolStripMenuItem.Enabled = this._hexBox.CanPaste();
            _selectAllToolStripMenuItem.Enabled = this._hexBox.CanSelectAll();
        }
        /// <summary>
        /// The handler for the "Cut"-Click event
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void CutMenuItem_Click(object sender, EventArgs e) { this._hexBox.Cut(); }
        /// <summary>
        /// The handler for the "Copy"-Click event
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void CopyMenuItem_Click(object sender, EventArgs e) { this._hexBox.Copy(); }
        /// <summary>
        /// The handler for the "Paste"-Click event
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void PasteMenuItem_Click(object sender, EventArgs e) { this._hexBox.Paste(); }
        /// <summary>
        /// The handler for the "Select All"-Click event
        /// </summary>
        /// <param name="sender">the sender object</param>
        /// <param name="e">the event data</param>
        void SelectAllMenuItem_Click(object sender, EventArgs e) { this._hexBox.SelectAll(); }
        /// <summary>
        /// Gets or sets the custom text of the "Copy" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null), Localizable(true)]
        public string CopyMenuItemText
        {
            get { return _copyMenuItemText; }
            set { _copyMenuItemText = value; }
        } string _copyMenuItemText;

        /// <summary>
        /// Gets or sets the custom text of the "Cut" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null), Localizable(true)]
        public string CutMenuItemText
        {
            get { return _cutMenuItemText; }
            set { _cutMenuItemText = value; }
        } string _cutMenuItemText;

        /// <summary>
        /// Gets or sets the custom text of the "Paste" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null), Localizable(true)]
        public string PasteMenuItemText
        {
            get { return _pasteMenuItemText; }
            set { _pasteMenuItemText = value; }
        } string _pasteMenuItemText;

        /// <summary>
        /// Gets or sets the custom text of the "Select All" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null), Localizable(true)]
        public string SelectAllMenuItemText
        {
            get { return _selectAllMenuItemText; }
            set { _selectAllMenuItemText = value; }
        } string _selectAllMenuItemText = null;

        /// <summary>
        /// Gets the text of the "Cut" ContextMenuStrip item.
        /// </summary>
        internal string CutMenuItemTextInternal { get { return !string.IsNullOrEmpty(CutMenuItemText) ? CutMenuItemText : "Cut"; } }
        /// <summary>
        /// Gets the text of the "Copy" ContextMenuStrip item.
        /// </summary>
        internal string CopyMenuItemTextInternal { get { return !string.IsNullOrEmpty(CopyMenuItemText) ? CopyMenuItemText : "Copy"; } }
        /// <summary>
        /// Gets the text of the "Paste" ContextMenuStrip item.
        /// </summary>
        internal string PasteMenuItemTextInternal { get { return !string.IsNullOrEmpty(PasteMenuItemText) ? PasteMenuItemText : "Paste"; } }
        /// <summary>
        /// Gets the text of the "Select All" ContextMenuStrip item.
        /// </summary>
        internal string SelectAllMenuItemTextInternal { get { return !string.IsNullOrEmpty(SelectAllMenuItemText) ? SelectAllMenuItemText : "SelectAll"; } }

        /// <summary>
        /// Gets or sets the image of the "Cut" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null)]
        public Image CutMenuItemImage
        {
            get { return _cutMenuItemImage; }
            set { _cutMenuItemImage = value; }
        } Image _cutMenuItemImage = null;
        /// <summary>
        /// Gets or sets the image of the "Copy" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null)]
        public Image CopyMenuItemImage
        {
            get { return _copyMenuItemImage; }
            set { _copyMenuItemImage = value; }
        } Image _copyMenuItemImage = null;
        /// <summary>
        /// Gets or sets the image of the "Paste" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null)]
        public Image PasteMenuItemImage
        {
            get { return _pasteMenuItemImage; }
            set { _pasteMenuItemImage = value; }
        } Image _pasteMenuItemImage = null;
        /// <summary>
        /// Gets or sets the image of the "Select All" ContextMenuStrip item.
        /// </summary>
        [Category("BuiltIn-ContextMenu"), DefaultValue(null)]
        public Image SelectAllMenuItemImage
        {
            get { return _selectAllMenuItemImage; }
            set { _selectAllMenuItemImage = value; }
        } Image _selectAllMenuItemImage = null;

        /// <summary>
        /// Applies dark or light mode colors to the context menu strip and its items.
        /// </summary>
        void ApplyDarkMode()
        {
            if (_contextMenuStrip == null)
                return;

            if (_isDarkMode)
            {
                _contextMenuStrip.Renderer = new DarkContextMenuRenderer();
                _contextMenuStrip.BackColor = Color.FromArgb(0x2D, 0x2D, 0x30);
                _contextMenuStrip.ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
                foreach (ToolStripItem item in _contextMenuStrip.Items)
                {
                    item.BackColor = Color.FromArgb(0x2D, 0x2D, 0x30);
                    item.ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
                }
            }
            else
            {
                _contextMenuStrip.Renderer = null;
                _contextMenuStrip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                _contextMenuStrip.BackColor = SystemColors.Control;
                _contextMenuStrip.ForeColor = SystemColors.ControlText;
                foreach (ToolStripItem item in _contextMenuStrip.Items)
                {
                    item.BackColor = SystemColors.Control;
                    item.ForeColor = SystemColors.ControlText;
                }
            }
        }

        /// <summary>
        /// Custom ToolStripRenderer for dark mode context menus.
        /// </summary>
        sealed class DarkContextMenuRenderer : ToolStripProfessionalRenderer
        {
            static readonly Color BackgroundColor = Color.FromArgb(0x2D, 0x2D, 0x30);
            static readonly Color BorderColor = Color.FromArgb(0x3F, 0x3F, 0x46);
            static readonly Color SeparatorColor = Color.FromArgb(0x3F, 0x3F, 0x46);
            static readonly Color HighlightColor = Color.FromArgb(0x3E, 0x3E, 0x42);
            static readonly Color TextColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
            static readonly Color DisabledTextColor = Color.FromArgb(0x65, 0x65, 0x69);
            static readonly Color ImageMarginColor = Color.FromArgb(0x2D, 0x2D, 0x30);

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(BackgroundColor))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                {
                    var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                    e.Graphics.DrawRectangle(pen, rect);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected && e.Item.Enabled)
                {
                    using (var brush = new SolidBrush(HighlightColor))
                    {
                        var rect = new Rectangle(2, 0, e.Item.Width - 3, e.Item.Height);
                        e.Graphics.FillRectangle(brush, rect);
                    }
                }
                else
                {
                    using (var brush = new SolidBrush(BackgroundColor))
                    {
                        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                    }
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? TextColor : DisabledTextColor;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new Pen(SeparatorColor))
                {
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
                }
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(ImageMarginColor))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }
        }
    }
}
