using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TrayView
{
    // A borderless panel that opens next to the notification area, like Windows 11's quick settings.
    //
    // It sits in the corner of the work area where the tray is: bottom right for a bottom taskbar,
    // top right for a top one, bottom left or right for a vertical one. It slides in from the
    // taskbar's side while fading in, and closes when it loses focus or on Escape.
    //
    // Every size comes from one DPI, the effective DPI of the monitor it opens on, through Px().
    class Flyout : Form
    {
        public float Zoom = 1;
        public int ClosedAt = -100000;
        public bool CloseOnDeactivate = true;
        public event Action Dismissed;
        // A normal window in the middle of the screen (the setup) rather than a tray flyout: it has
        // a taskbar button and isn't always on top. It rises from below as it fades in.
        protected bool Centered;

        protected Rectangle Area;
        protected AnchorStyles Edge = AnchorStyles.Bottom;
        int dpi;
        bool animating;

        public Flyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Text = Program.Name;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (Centered)
                    return cp;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab
                // WS_EX_TOPMOST here rather than TopMost = true: WinForms applies TopMost with a
                // SetWindowPos that activates the window, which would close any open flyout.
                cp.ExStyle |= 0x8;
                return cp;
            }
        }

        public int Px(float dip) { return (int)Math.Round(dip * Zoom); }

        // The flyout keeps this corner fixed: the one next to the tray.
        public bool AnchoredLeft { get { return Edge == AnchorStyles.Left; } }
        public bool AnchoredTop { get { return Edge == AnchorStyles.Top; } }

        protected void SetDpi(int value)
        {
            if (value == dpi)
                return;
            dpi = value;
            Zoom = value / 96f;
            OnZoomChanged();
        }

        // Called when the scale changes: on creation, when opening on another monitor, or when the
        // user changes the display scale while the flyout is open.
        protected virtual void OnZoomChanged() { }

        // The size, in pixels, the flyout wants at the current Zoom.
        protected virtual Size DesiredSize() { return Size; }

        // Before the opening animation, with the final bounds set: show the content to animate.
        protected virtual void OnOpening() { }
        protected virtual void OnOpened() { }
        protected virtual void OnDismissed() { }

        protected int Gap { get { return Px(12); } }

        // Where a flyout of this size goes, in the anchored corner of the work area.
        protected Rectangle Anchored(Size size)
        {
            if (Centered)
                return new Rectangle(Area.Left + (Area.Width - size.Width) / 2, Area.Top + (Area.Height - size.Height) / 2, size.Width, size.Height);
            int x = AnchoredLeft ? Area.Left + Gap : Area.Right - size.Width - Gap;
            int y = AnchoredTop ? Area.Top + Gap : Area.Bottom - size.Height - Gap;
            return new Rectangle(new Point(x, y), size);
        }

        // Opens on the monitor at the given point (the tray icon that was clicked).
        public void Open(Point near)
        {
            if (animating)
                return;
            if (Visible)
            {
                Activate();
                return;
            }
            Screen screen = Screen.FromPoint(near);
            Area = screen.WorkingArea;
            Edge = Centered ? AnchorStyles.Bottom : Native.TaskbarEdge(screen);
            SetDpi(Native.DpiAt(new Point(AnchoredLeft ? Area.Left : Area.Right - 1, AnchoredTop ? Area.Top : Area.Bottom - 1)));
            Bounds = Anchored(DesiredSize());
            OnOpening();
            Animate(true);
            // The click on the taskbar that opened this flyout can take the focus back right after
            // it was given; the flyout should still end up focused.
            if (Visible && Form.ActiveForm != this && !StayOpenOnDeactivate())
                Activate();
            OnOpened();
        }

        public void Dismiss()
        {
            if (!Visible || animating)
                return;
            Animate(false);
            ClosedAt = Environment.TickCount;
            OnDismissed();
            if (Dismissed != null)
                Dismissed();
        }

        // Keeps the anchored corner in place after the size changed.
        public void Reanchor()
        {
            if (Visible && !animating)
                Bounds = Anchored(Size);
        }

        // Slide and fade, timed by DWM's frames. Opening decelerates (fast, then settles); closing is
        // shorter and accelerates, like the Windows 11 flyouts. Off when the user turned off
        // animations in Windows.
        void Animate(bool opening)
        {
            animating = true;
            try
            {
                if (!Native.AnimationsEnabled())
                {
                    if (opening)
                    {
                        Show();
                        Activate();
                        DismissOthers();
                    }
                    else
                        Hide();
                    return;
                }
                Point final = Location;
                int distance = Px(opening ? 24 : 12), duration = opening ? 260 : 140;
                int dx = Edge == AnchorStyles.Left ? -1 : Edge == AnchorStyles.Right ? 1 : 0;
                int dy = Edge == AnchorStyles.Top ? -1 : Edge == AnchorStyles.Bottom ? 1 : 0;

                Native.SetLayered(Handle, true);
                Native.SetAlpha(Handle, opening ? (byte)0 : (byte)255);
                if (opening)
                {
                    Native.MoveWindow(Handle, final.X + dx * distance, final.Y + dy * distance);
                    Show();
                    Activate();
                    Refresh(); // paint now: the loop below keeps WM_PAINT from running until it ends
                    DismissOthers();
                }
                Stopwatch clock = Stopwatch.StartNew();
                while (true)
                {
                    double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
                    double shown = opening ? 1 - Math.Pow(1 - t, 4) : 1 - t * t;
                    double away = 1 - shown;
                    Native.MoveWindow(Handle, final.X + (int)Math.Round(dx * distance * away), final.Y + (int)Math.Round(dy * distance * away));
                    Native.SetAlpha(Handle, (byte)Math.Round(255 * Math.Min(1, shown * (opening ? 1.6 : 1))));
                    if (t >= 1)
                        break;
                    Native.WaitForFrame();
                }
                if (!opening)
                {
                    Hide();
                    Native.MoveWindow(Handle, final.X, final.Y);
                }
                Native.SetLayered(Handle, false);
            }
            finally { animating = false; }
        }

        // One flyout at a time, like the system's own. The others close once this one has the focus,
        // so they lose it first: closing the focused one would hand the focus to another app.
        void DismissOthers()
        {
            foreach (Form f in new List<Form>(Application.OpenForms.Cast<Form>()))
            {
                Flyout other = f as Flyout;
                if (other != null && other != this && other.Visible)
                    other.Dismiss();
            }
        }

        // Tells whether losing focus should leave the flyout open (for example, to a popup of its own).
        protected virtual bool StayOpenOnDeactivate() { return false; }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Not while the focus is still moving (inside another window's activation): checked once
            // it has settled.
            if (CloseOnDeactivate && !animating)
                BeginInvoke((Action)delegate
                {
                    if (Visible && Form.ActiveForm != this && !StayOpenOnDeactivate())
                        Dismiss();
                });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
                Dismiss();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                // The suggested rectangle keeps the old position; the corner anchor decides instead.
                SetDpi(m.WParam.ToInt32() & 0xFFFF);
                if (Visible && !animating)
                    Bounds = Anchored(DesiredSize());
                return;
            }
            base.WndProc(ref m);
        }
    }
}
