using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayView
{
    // Windows 11 (Fluent) colors, taken from the system light/dark mode and accent color.
    class Theme
    {
        public readonly bool Dark;
        public readonly Color Back, Text, Secondary, Strong, Disabled, Fill, FillFocused, Subtle, Stroke, Accent, OnAccent, Error;

        // Flyouts follow "Windows mode" (taskbar, quick settings) rather than "App mode".
        public Theme()
        {
            Dark = !IsLight("SystemUsesLightTheme");
            Color fg = Dark ? Color.White : Color.Black;
            Back = Dark ? Rgb(0x2C2C2C) : Rgb(0xF9F9F9);
            Text = Mix(Back, fg, Dark ? 1 : 0.9);
            Secondary = Mix(Back, fg, Dark ? 0.79 : 0.61);
            Strong = Mix(Back, fg, Dark ? 0.54 : 0.45);
            Disabled = Mix(Back, fg, Dark ? 0.36 : 0.37);
            Fill = Mix(Back, Color.White, Dark ? 0.06 : 0.7);
            FillFocused = Dark ? Rgb(0x1F1F1F) : Color.White;
            Subtle = Mix(Back, fg, Dark ? 0.06 : 0.04);
            Stroke = Mix(Back, fg, Dark ? 0.09 : 0.1);
            Accent = AccentShade(Dark ? 1 : 4);
            OnAccent = Dark ? Color.Black : Color.White;
            Error = Dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C);
        }

        public static bool IsLight(string value)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Equals(k.GetValue(value), 1);
        }

        // AccentPalette holds 8 RGBA shades from lightest to darkest; Windows uses
        // "Light 2" (1) on dark backgrounds and "Dark 1" (4) on light ones.
        static Color AccentShade(int index)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                byte[] p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                if (p == null || p.Length < 32)
                    return index < 3 ? Rgb(0x60CDFF) : Rgb(0x005FB8);
                return Color.FromArgb(p[index * 4], p[index * 4 + 1], p[index * 4 + 2]);
            }
        }

        public static Color Rgb(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // A flyout drawn entirely by hand in the Windows 11 style: a vertical stack of items
    // (text, toggles, segmented choices, text boxes, buttons). Painting is GDI+ on a solid background.
    class FluentPanel : Flyout
    {
        public readonly Theme Theme;
        public Font Body, Strong, Small, Glyphs;
        public Item Hot;
        public Point Mouse;
        protected int ContentWidth = 360;
        readonly List<Item> items = new List<Item>();
        Item pressed;

        public FluentPanel(Theme theme)
        {
            Theme = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Back;
            Icon = Native.AppIcon(32);
            SetDpi(Native.DpiAt(Cursor.Position));
        }

        // Layout and text are both sized in pixels from one DPI, the scale of the window's monitor,
        // so they always grow together (fonts in points would follow GDI's DPI, which can differ).
        protected override void OnZoomChanged()
        {
            Font[] old = { Body, Strong, Small, Glyphs };
            Body = MakeFont("Segoe UI Variable Text", "Segoe UI", 10.5f);
            Strong = MakeFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 10.5f);
            Small = MakeFont("Segoe UI Variable Small", "Segoe UI", 9f);
            Glyphs = MakeFont("Segoe Fluent Icons", "Segoe MDL2 Assets", 12f);
            Font = Body;
            foreach (Font font in old)
                if (font != null)
                    font.Dispose();
            if (items.Count > 0)
                Rescale();
        }

        Font MakeFont(string family, string fallback, float points)
        {
            float pixels = points * 96 / 72 * Zoom;
            try { return new Font(new FontFamily(family), pixels, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font(fallback, pixels, GraphicsUnit.Pixel); }
        }

        // Called after the scale changed; panels reload anything sized in pixels.
        protected virtual void Rescale()
        {
            Relayout();
        }

        public T Add<T>(T item) where T : Item
        {
            return Insert(items.Count, item);
        }

        public T Insert<T>(int index, T item) where T : Item
        {
            item.Owner = this;
            items.Insert(index, item);
            item.Attach();
            return item;
        }

        public void Remove(Item item)
        {
            if (items.Remove(item))
                item.Detach();
            if (Hot == item)
                Hot = null;
        }

        public int IndexOf(Item item) { return items.IndexOf(item); }

        // Stacks the visible items and sizes the window to fit them; the anchored corner stays put.
        public void Relayout()
        {
            int pad = Px(20), width = Px(ContentWidth), y = pad;
            bool first = true;
            foreach (Item item in items)
            {
                if (!item.Visible)
                {
                    item.Arrange();
                    continue;
                }
                if (!first)
                    y += Px(item.Gap);
                first = false;
                int h = item.Measure(width - 2 * pad);
                item.Bounds = new Rectangle(pad, y, width - 2 * pad, h);
                item.Arrange();
                y += h;
            }
            ClientSize = new Size(width, y + pad);
            Invalidate();
            Reanchor();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Item item in items)
                if (item.Visible)
                    item.Paint(e.Graphics);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            foreach (Item item in items)
                if (item.Visible && item.Enabled && item.Bounds.Contains(e.Location) && item.Press(e.Location))
                {
                    pressed = item;
                    Capture = true;
                    Invalidate();
                    return;
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Mouse = e.Location;
            if (pressed != null)
            {
                pressed.Drag(e.Location);
                Invalidate();
                return;
            }
            Item hot = null;
            foreach (Item item in items)
                if (item.Visible && item.Bounds.Contains(e.Location))
                    hot = item;
            if (hot != Hot || (hot != null && hot.TracksMouse))
            {
                Hot = hot;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Hot = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressed == null)
                return;
            Item item = pressed;
            pressed = null;
            item.Release(e.Location);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleFrame(Handle, Theme.Dark, Theme.Stroke);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Item item in items)
                    item.Detach();
                Body.Dispose(); Strong.Dispose(); Small.Dispose(); Glyphs.Dispose();
            }
            base.Dispose(disposing);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public void StrokeRound(Graphics g, Color c, RectangleF r, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, Math.Max(1, Zoom)))
                g.DrawPath(pen, p);
        }

        public void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, text, font, r, color, align | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    abstract class Item
    {
        public FluentPanel Owner;
        public Rectangle Bounds;
        public bool Visible = true, Enabled = true;
        public float Gap = 12;

        protected Theme T { get { return Owner.Theme; } }
        protected Color TextColor { get { return Enabled ? T.Text : T.Disabled; } }

        // Repaint on every mouse move over the item (for hover effects).
        public virtual bool TracksMouse { get { return false; } }

        public abstract int Measure(int width);
        public abstract void Paint(Graphics g);
        public virtual bool Press(Point p) { return false; }
        public virtual void Drag(Point p) { }
        public virtual void Release(Point p) { }

        // For items that host a real control: added to, laid out in and removed from the panel.
        public virtual void Attach() { }
        public virtual void Arrange() { }
        public virtual void Detach() { }
    }

    class TextItem : Item
    {
        public string Text;
        public Font Font;
        public Color? Tint;

        public TextItem(string text, Font font) { Text = text; Font = font; }

        // An empty text still takes one line, so showing a message later moves nothing.
        public override int Measure(int width)
        {
            return TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        public override void Paint(Graphics g)
        {
            Owner.DrawText(g, Text, Font, Tint ?? (Enabled ? T.Text : T.Disabled), Bounds, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
        }
    }

    class Divider : Item
    {
        public Divider() { Gap = 16; }
        public override int Measure(int width) { return Math.Max(1, Owner.Px(1)); }
        public override void Paint(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(T.Stroke))
                g.FillRectangle(b, Owner.ClientRectangle.X, Bounds.Y, Owner.ClientSize.Width, Bounds.Height);
        }
    }

    // Label on the left, Windows 11 toggle switch on the right; the whole row is clickable.
    class Toggle : Item
    {
        public string Text;
        public bool On;
        public event Action<bool> Changed;

        public Toggle(string text, bool on) { Text = text; On = on; }

        public override int Measure(int width) { return Owner.Px(24); }

        public override void Paint(Graphics g)
        {
            Owner.DrawText(g, Text, Owner.Body, TextColor, Bounds, TextFormatFlags.VerticalCenter);
            float w = Owner.Px(40), h = Owner.Px(20), x = Bounds.Right - w, y = Bounds.Y + (Bounds.Height - h) / 2;
            RectangleF track = new RectangleF(x, y, w, h);
            if (On)
                Owner.FillRound(g, Enabled ? T.Accent : T.Disabled, track, h / 2);
            else
                Owner.StrokeRound(g, Enabled ? T.Strong : T.Disabled, RectangleF.Inflate(track, -0.5f * Owner.Zoom, -0.5f * Owner.Zoom), h / 2);
            float knob = Owner.Px(12), kx = On ? x + w - h / 2 - knob / 2 : x + h / 2 - knob / 2;
            Owner.FillRound(g, On ? T.OnAccent : (Enabled ? T.Strong : T.Disabled), new RectangleF(kx, y + (h - knob) / 2, knob, knob), knob / 2);
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            if (!Bounds.Contains(p))
                return;
            On = !On;
            if (Changed != null)
                Changed(On);
        }
    }

    // Title row: an image (the app icon) and the title, with an optional icon button on the right.
    class TitleBar : Item
    {
        public string Text, Glyph;
        public Image Image;
        public event Action Clicked;

        public TitleBar(string text, string glyph) { Text = text; Glyph = glyph; Gap = 0; }

        public override bool TracksMouse { get { return true; } }

        public override int Measure(int width) { return Owner.Px(32); }

        Rectangle Button
        {
            get
            {
                int s = Owner.Px(32), overhang = Owner.Px(8);
                return new Rectangle(Bounds.Right - s + overhang, Bounds.Y, s, s);
            }
        }

        public override void Paint(Graphics g)
        {
            int x = Bounds.X;
            if (Image != null)
            {
                int s = Owner.Px(20);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Image, x, Bounds.Y + (Bounds.Height - s) / 2, s, s);
                x += Owner.Px(28);
            }
            Owner.DrawText(g, Text, Owner.Strong, T.Text, new Rectangle(x, Bounds.Y, Bounds.Right - x, Bounds.Height), TextFormatFlags.VerticalCenter);
            if (Glyph == null)
                return;
            Rectangle b = Button;
            if (Owner.Hot == this && b.Contains(Owner.Mouse))
                Owner.FillRound(g, T.Subtle, b, Owner.Px(4));
            Owner.DrawText(g, Glyph, Owner.Glyphs, T.Text, b, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public override bool Press(Point p) { return Glyph != null && Button.Contains(p); }

        public override void Release(Point p)
        {
            if (Button.Contains(p) && Clicked != null)
                Clicked();
        }
    }

    // A quiet one-line notice with an action on the right, such as an available update.
    class Notice : Item
    {
        public string Text, Link;
        public event Action Clicked;

        public Notice() { Gap = 16; }

        public override int Measure(int width) { return Owner.Px(36); }

        public override void Paint(Graphics g)
        {
            Owner.FillRound(g, T.Subtle, Bounds, Owner.Px(4));
            Rectangle inner = Rectangle.Inflate(Bounds, -Owner.Px(12), 0);
            Owner.DrawText(g, Text, Owner.Body, T.Text, inner, TextFormatFlags.VerticalCenter);
            Owner.DrawText(g, Link, Owner.Strong, T.Accent, inner, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            if (Bounds.Contains(p) && Clicked != null)
                Clicked();
        }
    }

    // A row of mutually exclusive options; the selected one is filled with the accent color.
    class Segmented : Item
    {
        public string Text;
        public string[] Options;
        public int Selected;
        public event Action<int> Changed;

        public Segmented(string text, string[] options, int selected) { Text = text; Options = options; Selected = selected; }

        public override int Measure(int width) { return Owner.Px(Text == null ? 32 : 60); }

        Rectangle Segment(int i)
        {
            int top = Bounds.Bottom - Owner.Px(32), w = Bounds.Width / Options.Length;
            return new Rectangle(Bounds.X + i * w, top, i == Options.Length - 1 ? Bounds.Right - Bounds.X - i * w : w, Owner.Px(32));
        }

        public override void Paint(Graphics g)
        {
            if (Text != null)
                Owner.DrawText(g, Text, Owner.Body, TextColor, new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, Owner.Px(20)), TextFormatFlags.VerticalCenter);
            RectangleF all = Rectangle.Union(Segment(0), Segment(Options.Length - 1));
            Owner.FillRound(g, T.Fill, all, Owner.Px(4));
            Owner.StrokeRound(g, T.Stroke, all, Owner.Px(4));
            for (int i = 0; i < Options.Length; i++)
            {
                Rectangle r = Segment(i);
                if (i == Selected && Enabled)
                    Owner.FillRound(g, T.Accent, RectangleF.Inflate(r, -Owner.Px(2), -Owner.Px(2)), Owner.Px(3));
                Color c = i == Selected && Enabled ? T.OnAccent : Enabled ? T.Text : T.Disabled;
                Owner.DrawText(g, Options[i], i == Selected ? Owner.Strong : Owner.Body, c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            for (int i = 0; i < Options.Length; i++)
                if (Segment(i).Contains(p) && i != Selected)
                {
                    Selected = i;
                    if (Changed != null)
                        Changed(i);
                }
        }
    }

    // Right-aligned buttons; the first one uses the accent color.
    class Buttons : Item
    {
        readonly string[] labels;
        readonly Action[] actions;

        public Buttons(string[] labels, Action[] actions) { this.labels = labels; this.actions = actions; Gap = 24; }

        public override int Measure(int width) { return Owner.Px(32); }

        Rectangle Button(int i)
        {
            int w = Owner.Px(120), gap = Owner.Px(8);
            return new Rectangle(Bounds.Right - (labels.Length - i) * w - (labels.Length - 1 - i) * gap, Bounds.Y, w, Bounds.Height);
        }

        public override void Paint(Graphics g)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = Button(i);
                bool accent = i == 0;
                Owner.FillRound(g, !Enabled ? T.Fill : accent ? T.Accent : T.Fill, r, Owner.Px(4));
                if (!accent || !Enabled)
                    Owner.StrokeRound(g, T.Stroke, r, Owner.Px(4));
                Owner.DrawText(g, labels[i], Owner.Body, !Enabled ? T.Disabled : accent ? T.OnAccent : T.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            for (int i = 0; i < labels.Length; i++)
                if (Button(i).Contains(p))
                    actions[i]();
        }
    }

    // A Windows 11 text box: a real Win32 edit control (typing, selection, clipboard, IME) inside a
    // hand-drawn frame, with the accent underline while it has focus and a gray placeholder.
    class TextBoxItem : Item
    {
        const int EM_SETCUEBANNER = 0x1501;
        public readonly TextBox Box = new TextBox();
        readonly string placeholder;
        public event Action Submitted;

        public TextBoxItem(string placeholder)
        {
            this.placeholder = placeholder;
            Box.BorderStyle = BorderStyle.None;
            Box.HandleCreated += delegate { SetPlaceholder(); };
            Box.GotFocus += delegate { Repaint(); };
            Box.LostFocus += delegate { Repaint(); };
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter)
                    return;
                e.SuppressKeyPress = true;
                if (Submitted != null)
                    Submitted();
            };
        }

        public string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        void SetPlaceholder()
        {
            IntPtr text = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(placeholder);
            Native.SendMessageW(Box.Handle, EM_SETCUEBANNER, new IntPtr(1), text);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(text);
        }

        void Repaint()
        {
            Box.BackColor = Box.Focused ? T.FillFocused : T.Fill;
            Owner.Invalidate(Bounds);
        }

        public override void Attach()
        {
            Box.ForeColor = T.Text;
            Box.BackColor = T.Fill;
            Owner.Controls.Add(Box);
        }

        public override void Detach()
        {
            Owner.Controls.Remove(Box);
        }

        public override int Measure(int width) { return Owner.Px(32); }

        public override void Arrange()
        {
            Box.Visible = Visible;
            if (!Visible)
                return;
            Box.Font = Owner.Body;
            int h = Owner.Body.Height, x = Owner.Px(11);
            Box.Bounds = new Rectangle(Bounds.X + x, Bounds.Y + (Bounds.Height - h) / 2 - Owner.Px(1), Bounds.Width - 2 * x, h);
        }

        public override void Paint(Graphics g)
        {
            bool focused = Box.Focused;
            RectangleF r = Bounds;
            Owner.FillRound(g, focused ? T.FillFocused : T.Fill, r, Owner.Px(4));
            Owner.StrokeRound(g, T.Stroke, r, Owner.Px(4));
            // The bottom edge: a thin line at rest, the accent color while typing.
            float line = focused ? Owner.Px(2) : Math.Max(1, Owner.Zoom);
            using (GraphicsPath clip = FluentPanel.Round(r, Owner.Px(4)))
            {
                Region old = g.Clip;
                g.SetClip(clip, CombineMode.Intersect);
                using (SolidBrush b = new SolidBrush(focused ? T.Accent : T.Strong))
                    g.FillRectangle(b, r.X, r.Bottom - line, r.Width, line);
                g.Clip = old;
            }
        }

        public override bool Press(Point p)
        {
            Box.Focus();
            return false;
        }
    }

    // A page in the settings list: its icon, name and address, with a remove button on the right.
    // Clicking the row starts editing its address.
    class SiteRow : Item
    {
        const string GlobeGlyph = "", DeleteGlyph = "";
        public readonly Site Site;
        public Image Image;
        public bool Selected;
        public event Action Clicked, Removed;

        public SiteRow(Site site, Image image) { Site = site; Image = image; Gap = 4; }

        public override bool TracksMouse { get { return true; } }

        public override int Measure(int width) { return Owner.Px(48); }

        Rectangle RemoveButton
        {
            get
            {
                int s = Owner.Px(32);
                return new Rectangle(Bounds.Right - s - Owner.Px(4), Bounds.Y + (Bounds.Height - s) / 2, s, s);
            }
        }

        public override void Paint(Graphics g)
        {
            bool hot = Owner.Hot == this;
            if (Selected || hot)
                Owner.FillRound(g, T.Subtle, Bounds, Owner.Px(4));
            if (Selected)
                Owner.FillRound(g, T.Accent, new RectangleF(Bounds.X, Bounds.Y + Owner.Px(14), Owner.Px(3), Bounds.Height - Owner.Px(28)), Owner.Px(1.5f));

            int s = Owner.Px(20), x = Bounds.X + Owner.Px(12);
            Rectangle icon = new Rectangle(x, Bounds.Y + (Bounds.Height - s) / 2, s, s);
            if (Image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Image, icon);
            }
            else
                Owner.DrawText(g, GlobeGlyph, Owner.Glyphs, T.Secondary, icon, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            Rectangle remove = RemoveButton;
            int textX = icon.Right + Owner.Px(12), textW = remove.X - Owner.Px(8) - textX, half = Bounds.Height / 2;
            Owner.DrawText(g, Site.Name, Owner.Body, T.Text, new Rectangle(textX, Bounds.Y + Owner.Px(4), textW, half - Owner.Px(4)), TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Owner.DrawText(g, Site.Url, Owner.Small, T.Secondary, new Rectangle(textX, Bounds.Y + half, textW, half - Owner.Px(4)), TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (hot && remove.Contains(Owner.Mouse))
                Owner.FillRound(g, T.Subtle, remove, Owner.Px(4));
            Owner.DrawText(g, DeleteGlyph, Owner.Glyphs, hot ? T.Text : T.Secondary, remove, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            if (!Bounds.Contains(p))
                return;
            if (RemoveButton.Contains(p))
            {
                if (Removed != null)
                    Removed();
            }
            else if (Clicked != null)
                Clicked();
        }

        public override void Detach()
        {
            if (Image != null)
                Image.Dispose();
            Image = null;
        }
    }
}
