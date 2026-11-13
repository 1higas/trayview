using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayView
{
    // The flyout that shows a page. The browser is created the first time it opens and destroyed a
    // while after it closes (Settings > Free memory after a page closes), so an idle page costs no
    // memory. Its two edges and the corner away from the tray resize it like a normal window; the
    // size is saved per page.
    class SiteView : Flyout
    {
        public readonly Site Page;
        readonly TrayApp app;
        readonly Timer unloadTimer = new Timer();
        readonly List<Action<Browser>> pending = new List<Action<Browser>>();
        readonly Grip[] grips = new Grip[3];
        readonly Timer spinTimer = new Timer();
        readonly Stopwatch spinClock = Stopwatch.StartNew();
        Browser browser;
        bool creating, preloading;
        bool loading = true; // no content yet: the browser stays hidden and a progress ring spins

        public SiteView(TrayApp app, Site site)
        {
            this.app = app;
            Page = site;
            BackColor = app.Theme.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            spinTimer.Interval = 15;
            spinTimer.Tick += delegate { Invalidate(SpinnerBounds); };
            unloadTimer.Tick += delegate
            {
                unloadTimer.Stop();
                if (!Visible)
                    Unload();
            };
            CreateHandle();
            for (int i = 0; i < grips.Length; i++)
                grips[i] = new Grip(this);
        }

        public bool Loaded { get { return browser != null; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleFrame(Handle, app.Theme.Dark, app.Theme.Stroke);
        }

        public void ApplyTheme(Theme theme)
        {
            BackColor = theme.Back;
            Native.StyleFrame(Handle, theme.Dark, theme.Stroke);
        }

        protected override Size DesiredSize()
        {
            return Fit(new Size(Px(Page.Width), Px(Page.Height)));
        }

        Size MinSize { get { return new Size(Px(TrayView.Site.MinWidth), Px(TrayView.Site.MinHeight)); } }
        Size MaxSize { get { return new Size(Area.Width - 2 * Gap, Area.Height - 2 * Gap); } }

        Size Fit(Size s)
        {
            Size min = MinSize, max = MaxSize;
            return new Size(Math.Max(Math.Min(min.Width, max.Width), Math.Min(s.Width, max.Width)),
                Math.Max(Math.Min(min.Height, max.Height), Math.Min(s.Height, max.Height)));
        }

        // The page is shown before the animation so it slides in with the flyout instead of
        // appearing after it.
        protected override void OnOpening()
        {
            unloadTimer.Stop();
            preloading = false;
            CloseOnDeactivate = app.Settings.CloseOnFocusLoss;
            ArrangeGrips();
            if (browser != null)
            {
                browser.LowMemory = false;
                browser.Visible = !loading;
            }
        }

        protected override void OnOpened()
        {
            UpdateSpinner();
            WithBrowser(b =>
            {
                b.Visible = !loading;
                if (!loading)
                    b.Focus();
            });
        }

        // The address shown now (links followed inside the page change it), or the saved one.
        public string CurrentUrl
        {
            get
            {
                string url = browser != null ? browser.Source : null;
                return string.IsNullOrEmpty(url) || url == "about:blank" ? Page.Url : url;
            }
        }

        // Windows 11's indeterminate progress ring: an accent arc that turns while it grows and shrinks.
        Rectangle SpinnerBounds
        {
            get
            {
                int s = Px(40);
                return new Rectangle((ClientSize.Width - s) / 2, (ClientSize.Height - s) / 2, s, s);
            }
        }

        void UpdateSpinner()
        {
            spinTimer.Enabled = loading && Visible;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!loading)
                return;
            double t = spinClock.Elapsed.TotalSeconds;
            float sweep = (float)(30 + 240 * (0.5 - 0.5 * Math.Cos(t * Math.PI / 0.9)));
            float start = (float)(t * 360 % 360) + (float)(t * 140 % 360) - sweep / 2;
            float stroke = Px(4);
            RectangleF ring = SpinnerBounds;
            ring.Inflate(-stroke / 2 - Px(2), -stroke / 2 - Px(2));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(app.Theme.Accent, stroke))
            {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.DrawArc(pen, ring, start, sweep);
            }
        }

        protected override void OnDismissed()
        {
            spinTimer.Stop();
            if (browser != null)
            {
                browser.Visible = false;
                browser.LowMemory = true;
            }
            ScheduleUnload(app.Settings.UnloadMinutes * 60000);
        }

        void ScheduleUnload(int ms)
        {
            unloadTimer.Stop();
            if (browser == null && !creating)
                return;
            if (ms < 0)
                return;
            if (ms == 0)
            {
                Unload();
                return;
            }
            unloadTimer.Interval = ms;
            unloadTimer.Start();
        }

        // Loads the page out of sight just to get its favicon and title, then lets it go.
        public void Preload()
        {
            if (Visible || browser != null || creating)
                return;
            preloading = true;
            WithBrowser(b => { });
            ScheduleUnload(30000); // in case the page has no favicon
        }

        // The pointer is on the tray icon: start the browser now so the page is (nearly) ready by the
        // time it is clicked. If it isn't opened, it is let go again after a short while.
        public void Warm()
        {
            if (Visible || browser != null || creating)
                return;
            WithBrowser(b => { });
            if (app.Settings.UnloadMinutes >= 0)
                ScheduleUnload(30000);
        }

        public void Navigate(string url)
        {
            if (browser != null)
                browser.Navigate(url);
            else
                Preload();
        }

        public void Reload()
        {
            if (browser != null)
                browser.Reload();
        }

        public void Unload()
        {
            unloadTimer.Stop();
            if (browser == null)
                return;
            browser.Close();
            browser = null;
            loading = true;
            Native.TrimMemory();
        }

        void WithBrowser(Action<Browser> action)
        {
            if (browser != null)
            {
                action(browser);
                return;
            }
            pending.Add(action);
            if (creating)
                return;
            creating = true;
            Browser.Create(Handle, BackColor, (b, error) =>
            {
                creating = false;
                if (IsDisposed)
                {
                    if (b != null)
                        b.Close();
                    return;
                }
                if (b == null)
                {
                    pending.Clear();
                    spinTimer.Stop();
                    app.ShowError(error);
                    return;
                }
                browser = b;
                loading = true;
                b.Bounds = ClientRectangle;
                b.Visible = false;
                b.NavigationCompleted += delegate
                {
                    if (browser != b)
                        return;
                    // Some pages never report a favicon (single-page apps that replace their icon
                    // links, for one): ask the browser again, and fetch the site's own as a fallback.
                    if (Page.Favicon == null)
                    {
                        b.RequestFavicon();
                        app.FetchFavicon(Page);
                    }
                    if (!loading)
                        return;
                    loading = false;
                    b.Visible = Visible;
                    if (Visible)
                        b.Focus();
                    UpdateSpinner();
                };
                b.TitleChanged += title => app.OnTitle(Page, title);
                b.FaviconChanged += png =>
                {
                    app.OnFavicon(Page, png);
                    if (preloading && !Visible)
                    {
                        preloading = false;
                        ScheduleUnload(3000); // the title usually comes with or before the icon
                    }
                };
                b.NewWindowRequested += OpenExternally;
                b.EscapePressed += () => BeginInvoke((Action)Dismiss);
                b.Navigate(Page.Url);
                ArrangeGrips();
                Action<Browser>[] actions = pending.ToArray();
                pending.Clear();
                foreach (Action<Browser> a in actions)
                    a(b);
            });
        }

        static void OpenExternally(string uri)
        {
            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed))
                return;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeMailto)
                return;
            try { Process.Start(parsed.AbsoluteUri); }
            catch (Exception) { }
        }

        // A context menu or another popup of the page (owned by the browser process) takes the focus
        // without meaning "close".
        protected override bool StayOpenOnDeactivate()
        {
            if (browser == null)
                return false;
            int pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return pid != 0 && pid == browser.ProcessId;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (browser != null)
                browser.Bounds = ClientRectangle;
            ArrangeGrips();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (browser != null)
                browser.ParentMoved();
        }

        // Thin invisible handles on the two free edges and the free corner, above the page.
        void ArrangeGrips()
        {
            if (grips[0] == null)
                return;
            int w = ClientSize.Width, h = ClientSize.Height, edge = Px(6), corner = Px(16);
            bool freeLeft = !AnchoredLeft, freeTop = !AnchoredTop;
            int ex = freeLeft ? 0 : w - edge, ey = freeTop ? 0 : h - edge;
            int cx = freeLeft ? 0 : w - corner, cy = freeTop ? 0 : h - corner;
            grips[0].Place(new Rectangle(ex, 0, edge, h), freeLeft ? Native.HTLEFT : Native.HTRIGHT);
            grips[1].Place(new Rectangle(0, ey, w, edge), freeTop ? Native.HTTOP : Native.HTBOTTOM);
            grips[2].Place(new Rectangle(cx, cy, corner, corner),
                freeTop ? (freeLeft ? Native.HTTOPLEFT : Native.HTTOPRIGHT) : (freeLeft ? Native.HTBOTTOMLEFT : Native.HTBOTTOMRIGHT));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_GETMINMAXINFO)
            {
                // MINMAXINFO: ptMinTrackSize at offset 24, ptMaxTrackSize at 32.
                Size min = MinSize, max = MaxSize;
                if (max.Width > 0 && max.Height > 0)
                {
                    Marshal.WriteInt32(m.LParam, 24, Math.Min(min.Width, max.Width));
                    Marshal.WriteInt32(m.LParam, 28, Math.Min(min.Height, max.Height));
                    Marshal.WriteInt32(m.LParam, 32, max.Width);
                    Marshal.WriteInt32(m.LParam, 36, max.Height);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Native.WM_EXITSIZEMOVE)
            {
                Page.Width = (int)Math.Round(ClientSize.Width / Zoom);
                Page.Height = (int)Math.Round(ClientSize.Height / Zoom);
                app.Settings.SaveSite(Page);
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Unload();
                unloadTimer.Dispose();
                foreach (Grip g in grips)
                    if (g != null)
                        g.DestroyHandle();
            }
            base.Dispose(disposing);
        }
    }

    // An invisible child window that starts the system's resize loop on its parent when pressed,
    // with the matching resize cursor. Child windows sit above the browser's own window, which a
    // hit test on the parent alone could not reach.
    class Grip : NativeWindow
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_EX_LAYERED = 0x80000;
        const int IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEWE = 32644, IDC_SIZENS = 32645;
        readonly Control owner;
        int hit;

        public Grip(Control owner)
        {
            this.owner = owner;
            CreateParams cp = new CreateParams();
            cp.Parent = owner.Handle;
            cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            cp.ExStyle = WS_EX_LAYERED;
            CreateHandle(cp);
            Native.SetAlpha(Handle, 1); // practically invisible, but still takes the mouse
        }

        public void Place(Rectangle r, int hitTest)
        {
            hit = hitTest;
            Native.PlaceOnTop(Handle, r);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_SETCURSOR)
            {
                int cursor = hit == Native.HTLEFT || hit == Native.HTRIGHT ? IDC_SIZEWE
                    : hit == Native.HTTOP || hit == Native.HTBOTTOM ? IDC_SIZENS
                    : hit == Native.HTTOPLEFT || hit == Native.HTBOTTOMRIGHT ? IDC_SIZENWSE : IDC_SIZENESW;
                Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, new IntPtr(cursor)));
                m.Result = new IntPtr(1);
                return;
            }
            if (m.Msg == Native.WM_LBUTTONDOWN)
            {
                Native.ReleaseCapture();
                Native.SendMessageW(owner.Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(hit), IntPtr.Zero);
                return;
            }
            base.WndProc(ref m);
        }
    }
}
