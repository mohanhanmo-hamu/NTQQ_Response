using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQToastTray
{
    public class FluentPalette
    {
        public bool IsDark;
        public Color WindowBg;
        public Color CardBg;
        public Color CardBorder;
        public Color RowHoverBg;
        public Color TextPrimary;
        public Color TextSecondary;
        public Color Accent;
        public Color AccentHover;
        public Color AccentText;
        public Color ToggleOffBg;
        public Color ToggleOffBorder;
        public Color ToggleKnobOff;
        public Color ToggleKnobOn;
        public Color SuccessBg;
        public Color SuccessFg;
        public Color WarnBg;
        public Color WarnFg;
        public Color WarnBorder;
        public Color SubtleBtnBg;
        public Color SubtleBtnHover;
        public Color SubtleBtnBorder;
        public Color SegBg;
        public Color SegActiveBg;

        public static bool IsSystemDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val is int) return ((int)val) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static FluentPalette Current()
        {
            bool dark = IsSystemDarkMode();
            if (dark)
            {
                return new FluentPalette
                {
                    IsDark = true,
                    WindowBg = Color.FromArgb(32, 32, 32),
                    CardBg = Color.FromArgb(43, 43, 43),
                    CardBorder = Color.FromArgb(58, 58, 58),
                    RowHoverBg = Color.FromArgb(54, 54, 54),
                    TextPrimary = Color.FromArgb(245, 245, 245),
                    TextSecondary = Color.FromArgb(168, 168, 168),
                    Accent = Color.FromArgb(76, 194, 255),
                    AccentHover = Color.FromArgb(105, 206, 255),
                    AccentText = Color.FromArgb(16, 16, 16),
                    ToggleOffBg = Color.FromArgb(50, 50, 50),
                    ToggleOffBorder = Color.FromArgb(150, 150, 150),
                    ToggleKnobOff = Color.FromArgb(205, 205, 205),
                    ToggleKnobOn = Color.FromArgb(16, 16, 16),
                    SuccessBg = Color.FromArgb(31, 58, 39),
                    SuccessFg = Color.FromArgb(108, 203, 95),
                    WarnBg = Color.FromArgb(67, 53, 25),
                    WarnFg = Color.FromArgb(252, 225, 0),
                    WarnBorder = Color.FromArgb(110, 88, 36),
                    SubtleBtnBg = Color.FromArgb(54, 54, 54),
                    SubtleBtnHover = Color.FromArgb(66, 66, 66),
                    SubtleBtnBorder = Color.FromArgb(72, 72, 72),
                    SegBg = Color.FromArgb(34, 34, 34),
                    SegActiveBg = Color.FromArgb(62, 62, 62)
                };
            }
            return new FluentPalette
            {
                IsDark = false,
                WindowBg = Color.FromArgb(243, 243, 243),
                CardBg = Color.FromArgb(255, 255, 255),
                CardBorder = Color.FromArgb(229, 229, 229),
                RowHoverBg = Color.FromArgb(246, 246, 246),
                TextPrimary = Color.FromArgb(26, 26, 26),
                TextSecondary = Color.FromArgb(106, 106, 106),
                Accent = Color.FromArgb(0, 103, 192),
                AccentHover = Color.FromArgb(25, 117, 197),
                AccentText = Color.White,
                ToggleOffBg = Color.FromArgb(248, 248, 248),
                ToggleOffBorder = Color.FromArgb(134, 134, 134),
                ToggleKnobOff = Color.FromArgb(92, 92, 92),
                ToggleKnobOn = Color.White,
                SuccessBg = Color.FromArgb(223, 246, 221),
                SuccessFg = Color.FromArgb(15, 123, 15),
                WarnBg = Color.FromArgb(255, 244, 206),
                WarnFg = Color.FromArgb(157, 93, 0),
                WarnBorder = Color.FromArgb(236, 211, 140),
                SubtleBtnBg = Color.FromArgb(251, 251, 251),
                SubtleBtnHover = Color.FromArgb(240, 240, 240),
                SubtleBtnBorder = Color.FromArgb(218, 218, 218),
                SegBg = Color.FromArgb(238, 238, 238),
                SegActiveBg = Color.White
            };
        }

        public static int ToColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }

        public static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class FluentCard : Panel
    {
        public FluentPalette Palette;
        public int CornerRadius = 8;

        public FluentCard(FluentPalette palette)
        {
            Palette = palette;
            DoubleBuffered = true;
            BackColor = palette.WindowBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = FluentPalette.CreateRoundedRect(r, CornerRadius))
            using (SolidBrush bg = new SolidBrush(Palette.CardBg))
            using (Pen border = new Pen(Palette.CardBorder, 1f))
            {
                e.Graphics.FillPath(bg, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    public class FluentToggleRow : Control
    {
        private bool _checked;
        private bool _hovered;
        private readonly FluentPalette _palette;
        public string Title;
        public string Subtitle;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                    if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
                }
            }
        }

        public void SetCheckedSilent(bool value)
        {
            if (_checked != value)
            {
                _checked = value;
                Invalidate();
            }
        }

        public FluentToggleRow(FluentPalette palette, string title, string subtitle)
        {
            _palette = palette;
            Title = title;
            Subtitle = subtitle;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = string.IsNullOrEmpty(subtitle) ? 34 : 44;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(_palette.CardBg))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            if (_hovered)
            {
                Rectangle hr = new Rectangle(4, 2, Width - 9, Height - 5);
                using (var hp = FluentPalette.CreateRoundedRect(hr, 6))
                using (var hb = new SolidBrush(_palette.RowHoverBg))
                {
                    g.FillPath(hb, hp);
                }
            }

            int toggleW = 38;
            int toggleH = 20;
            int toggleX = Width - toggleW - 14;
            int toggleY = (Height - toggleH) / 2;

            // Text
            using (var titleFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular))
            using (var titleBrush = new SolidBrush(_palette.TextPrimary))
            {
                if (string.IsNullOrEmpty(Subtitle))
                {
                    SizeF sz = g.MeasureString(Title, titleFont);
                    g.DrawString(Title, titleFont, titleBrush, 12, (Height - sz.Height) / 2f);
                }
                else
                {
                    g.DrawString(Title, titleFont, titleBrush, 12, 6);
                    using (var subFont = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
                    using (var subBrush = new SolidBrush(_palette.TextSecondary))
                    {
                        g.DrawString(Subtitle, subFont, subBrush, 12, 23);
                    }
                }
            }

            // Win11 Pill Toggle
            Rectangle tr = new Rectangle(toggleX, toggleY, toggleW, toggleH);
            using (var tp = FluentPalette.CreateRoundedRect(tr, toggleH / 2))
            {
                if (_checked)
                {
                    using (var fill = new SolidBrush(_palette.Accent)) g.FillPath(fill, tp);
                }
                else
                {
                    using (var fill = new SolidBrush(_palette.ToggleOffBg)) g.FillPath(fill, tp);
                    using (var pen = new Pen(_palette.ToggleOffBorder, 1.2f)) g.DrawPath(pen, tp);
                }
            }

            int knobSize = 12;
            int knobX = _checked ? (toggleX + toggleW - knobSize - 4) : (toggleX + 4);
            int knobY = toggleY + (toggleH - knobSize) / 2;
            using (var kb = new SolidBrush(_checked ? _palette.ToggleKnobOn : _palette.ToggleKnobOff))
            {
                g.FillEllipse(kb, knobX, knobY, knobSize, knobSize);
            }
        }
    }

    public class FluentSegmentedControl : Control
    {
        private readonly FluentPalette _palette;
        private readonly string[] _labels;
        private int _selectedIndex;
        private int _hoverIndex = -1;
        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value >= 0 && value < _labels.Length && _selectedIndex != value)
                {
                    _selectedIndex = value;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public void SetSelectedSilent(int index)
        {
            if (index >= 0 && index < _labels.Length && _selectedIndex != index)
            {
                _selectedIndex = index;
                Invalidate();
            }
        }

        public FluentSegmentedControl(FluentPalette palette, string[] labels, int initialIndex)
        {
            _palette = palette;
            _labels = labels;
            _selectedIndex = initialIndex;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 28;
        }

        private int HitTest(int x)
        {
            if (_labels.Length == 0 || Width <= 0) return -1;
            int idx = (x * _labels.Length) / Width;
            if (idx < 0) idx = 0;
            if (idx >= _labels.Length) idx = _labels.Length - 1;
            return idx;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = HitTest(e.X);
            if (idx != _hoverIndex) { _hoverIndex = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIndex = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.X);
                if (idx >= 0) SelectedIndex = idx;
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(_palette.CardBg)) g.FillRectangle(bg, ClientRectangle);

            Rectangle outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var op = FluentPalette.CreateRoundedRect(outer, 6))
            using (var ob = new SolidBrush(_palette.SegBg))
            {
                g.FillPath(ob, op);
            }

            if (_labels.Length == 0) return;
            float segW = (Width - 4f) / _labels.Length;

            using (var font = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Regular))
            using (var boldFont = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Bold))
            {
                for (int i = 0; i < _labels.Length; i++)
                {
                    Rectangle segR = new Rectangle((int)(2 + i * segW), 2, (int)segW - 1, Height - 5);
                    bool selected = (i == _selectedIndex);
                    if (selected)
                    {
                        using (var sp = FluentPalette.CreateRoundedRect(segR, 5))
                        using (var sb = new SolidBrush(_palette.SegActiveBg))
                        using (var pen = new Pen(_palette.CardBorder, 1f))
                        {
                            g.FillPath(sb, sp);
                            g.DrawPath(pen, sp);
                        }
                    }
                    else if (i == _hoverIndex)
                    {
                        using (var sp = FluentPalette.CreateRoundedRect(segR, 5))
                        using (var sb = new SolidBrush(_palette.RowHoverBg))
                        {
                            g.FillPath(sb, sp);
                        }
                    }

                    Font useFont = selected ? boldFont : font;
                    Color fg = selected ? _palette.Accent : _palette.TextSecondary;
                    using (var tb = new SolidBrush(fg))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(_labels[i], useFont, tb, segR, sf);
                    }
                }
            }
        }
    }

    public class FluentButton : Control
    {
        private readonly FluentPalette _palette;
        private readonly bool _primary;
        private bool _hovered;
        public string ButtonText;

        public FluentButton(FluentPalette palette, string text, bool primary)
        {
            _palette = palette;
            ButtonText = text;
            _primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 32;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color parentBg = Parent != null ? Parent.BackColor : _palette.WindowBg;
            using (var pbg = new SolidBrush(parentBg)) g.FillRectangle(pbg, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fillColor = _primary
                ? (_hovered ? _palette.AccentHover : _palette.Accent)
                : (_hovered ? _palette.SubtleBtnHover : _palette.SubtleBtnBg);
            Color textColor = _primary ? _palette.AccentText : _palette.TextPrimary;

            using (var path = FluentPalette.CreateRoundedRect(r, 6))
            using (var fill = new SolidBrush(fillColor))
            {
                g.FillPath(fill, path);
                if (!_primary)
                {
                    using (var pen = new Pen(_palette.SubtleBtnBorder, 1f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            using (var font = new Font("Microsoft YaHei UI", 8.6f, _primary ? FontStyle.Bold : FontStyle.Regular))
            using (var tb = new SolidBrush(textColor))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(ButtonText, font, tb, r, sf);
            }
        }
    }
}
