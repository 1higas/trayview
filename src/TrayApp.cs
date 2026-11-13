using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayView
{
    // Lives in the notification area with one icon per page. Left click opens the page; right click
    // offers reload, open in browser, settings and exit. With no page set up, a single globe icon
    // opens the onboarding instead. Nothing runs in the background while pages are closed.
    class TrayApp : ApplicationContext
    {
        public readonly Settings Settings = Settings.Load();
        public Theme Theme = new Theme();
        public event Action Changed; // pages were added, removed or renamed, or got a new icon

        readonly List<PageIcon> pages = new List<PageIcon>();
        readonly Control invoker = new Control();
        readonly RegisteredWaitHandle showRequests, exitRequests;
        NotifyIcon setupIcon;
        Icon placeholder;
        AppPanel panel;
        int panelClosedAt = -100000;
        readonly HashSet<string> fetchingFavicon = new HashSet<string>();
        bool checkingForUpdate;

        // showRequest: TrayView was started again. exitRequest: the setup is about to replace it.
        public TrayApp(bool interactive, WaitHandle showRequest, WaitHandle exitRequest)
        {
            GC.KeepAlive(invoker.Handle); // creates the window BeginInvoke posts to
            showRequests = ThreadPool.RegisterWaitForSingleObject(showRequest, delegate { invoker.BeginInvoke((Action)ShowMain); }, null, -1, false);
            exitRequests = ThreadPool.RegisterWaitForSingleObject(exitRequest, delegate { invoker.BeginInvoke((Action)Exit); }, null, -1, true);
            SystemEvents.UserPreferenceChanged += OnSystemChange;
            SystemEvents.DisplaySettingsChanged += OnSystemChange;

            foreach (Site site in Settings.Sites)
                pages.Add(new PageIcon(this, site));
            SyncSetupIcon();
            if (interactive && pages.Count == 0)
                ShowOnboarding();
            foreach (PageIcon page in pages)
                if (page.Site.Favicon == null)
                    page.View.Preload();
            Native.TrimMemory();
        }

        // The tray icon size follows the taskbar's scale, which can change while the app runs.
        public Icon Placeholder
        {
            get
            {
                int size = Native.TrayIconSize();
                if (placeholder == null || placeholder.Width != size)
                {
                    Icons.Destroy(placeholder);
                    placeholder = Icons.Placeholder(size);
                }
                return placeholder;
            }
        }

        // Starting the app again while it runs: the onboarding, or the settings.
        void ShowMain()
        {
            if (pages.Count == 0)
                ShowOnboarding();
            else
                ShowSettings();
        }

        public void ShowOnboarding() { TogglePanel(typeof(OnboardingPanel), false); }
        public void ShowSettings() { TogglePanel(typeof(SettingsPanel), false); }

        void TogglePanel(Type type, bool toggle)
        {
            if (panel != null && panel.GetType() == type && panel.Visible)
            {
                if (toggle)
                    panel.Dismiss();
                else
                    panel.Activate();
                return;
            }
            if (toggle && Environment.TickCount - panelClosedAt < 300) // the click that closed it by deactivation
                return;
            if (panel != null)
                panel.Close();
            AppPanel p = type == typeof(OnboardingPanel) ? (AppPanel)new OnboardingPanel(this) : new SettingsPanel(this);
            panel = p;
            p.Dismissed += delegate
            {
                panelClosedAt = Environment.TickCount;
                invoker.BeginInvoke((Action)delegate
                {
                    p.Close();
                    if (panel == p)
                        panel = null;
                    Native.TrimMemory();
                });
            };
            p.Open(Cursor.Position);
            if (type == typeof(SettingsPanel))
                CheckForUpdate();
        }

        public void CompleteOnboarding(string url, bool autostart)
        {
            SetAutostart(autostart);
            PageIcon page = AddSite(url, false);
            if (panel != null)
                panel.Dismiss();
            page.View.Open(Cursor.Position);
        }

        public PageIcon AddSite(string url, bool preload)
        {
            PageIcon page = new PageIcon(this, Settings.NewSite(url));
            pages.Add(page);
            SyncSetupIcon();
            if (preload)
                page.View.Preload();
            RaiseChanged();
            return page;
        }

        public void ChangeSiteUrl(Site site, string url)
        {
            if (site.Url == url)
                return;
            site.Url = url;
            site.Title = null;
            site.Favicon = null;
            Settings.SaveSite(site);
            PageIcon page = Find(site);
            page.Refresh();
            page.View.Navigate(url);
            RaiseChanged();
        }

        public void RemoveSite(Site site)
        {
            PageIcon page = Find(site);
            Settings.DeleteSite(site);
            pages.Remove(page);
            page.Dispose();
            SyncSetupIcon();
            RaiseChanged();
        }

        public void OnFavicon(Site site, byte[] png)
        {
            if (site.Favicon != null && site.Favicon.SequenceEqual(png))
                return;
            site.Favicon = png;
            Settings.SaveSite(site);
            Find(site).Refresh();
            RaiseChanged();
        }

        // Downloads the site's /favicon.ico, used when the page reports no favicon. A favicon from the
        // page itself, if one arrives meanwhile, wins.
        public void FetchFavicon(Site site)
        {
            string url = site.Url;
            if (fetchingFavicon.Contains(url))
                return;
            fetchingFavicon.Add(url);
            string address = new Uri(url).GetLeftPart(UriPartial.Authority) + "/favicon.ico";
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] png = null;
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
                    request.UserAgent = Program.Name + "/" + Program.Version;
                    request.Timeout = request.ReadWriteTimeout = 10000;
                    using (WebResponse response = request.GetResponse())
                    using (System.IO.Stream body = response.GetResponseStream())
                    using (System.IO.MemoryStream data = new System.IO.MemoryStream())
                    {
                        body.CopyTo(data);
                        png = Icons.ToPng(data.ToArray());
                    }
                }
                catch (Exception) { }
                try
                {
                    invoker.BeginInvoke((Action)delegate
                    {
                        fetchingFavicon.Remove(url);
                        if (png != null && site.Favicon == null && site.Url == url && pages.Any(p => p.Site == site))
                            OnFavicon(site, png);
                    });
                }
                catch (Exception) { } // exiting
            });
        }

        public void OnTitle(Site site, string title)
        {
            title = (title ?? "").Trim();
            // Before a page sets its title, WebView2 reports the address; keep the host name then.
            if (title.Length == 0 || title == site.Title || title.Contains("://") || title == site.Host)
                return;
            site.Title = title;
            Settings.SaveSite(site);
            Find(site).Refresh();
            RaiseChanged();
        }

        public void ShowError(string message)
        {
            MessageBox.Show(message, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public void SetAutostart(bool on)
        {
            try { Settings.Autostart = on; }
            catch (Exception e) { ShowError("Could not change Start with Windows: " + e.Message); }
        }

        PageIcon Find(Site site)
        {
            return pages.First(p => p.Site == site);
        }

        void RaiseChanged()
        {
            if (Changed != null)
                Changed();
        }

        // The globe icon that stands in while no page is set up.
        void SyncSetupIcon()
        {
            if (pages.Count == 0 && setupIcon == null)
            {
                setupIcon = new NotifyIcon();
                setupIcon.Icon = Placeholder;
                setupIcon.Text = Program.Name + ": click to add a page";
                setupIcon.ContextMenu = new ContextMenu(new[]
                {
                    new MenuItem("Add a page", delegate { ShowOnboarding(); }) { DefaultItem = true },
                    new MenuItem("-"),
                    new MenuItem("Exit", delegate { Exit(); }),
                });
                setupIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(typeof(OnboardingPanel), true); };
                setupIcon.Visible = true;
            }
            else if (pages.Count > 0 && setupIcon != null)
            {
                setupIcon.Visible = false;
                setupIcon.Dispose();
                setupIcon = null;
            }
        }

        public void SetCheckUpdates(bool on)
        {
            Settings.SaveCheckUpdates(on);
            CheckForUpdate();
            SettingsPanel sp = panel as SettingsPanel;
            if (sp != null)
                sp.UpdateFound();
        }

        // The newer release seen on GitHub, if any (and if update checks are on).
        public string UpdateVersion
        {
            get
            {
                Version latest;
                return Settings.CheckUpdates && Version.TryParse(Settings.LatestVersion, out latest) && latest > new Version(Program.Version)
                    ? Settings.LatestVersion : null;
            }
        }

        // Asks GitHub for the latest release when the settings open, at most once a day, on a
        // background thread with a short timeout. Failures are ignored and retried next time.
        void CheckForUpdate()
        {
            if (!Settings.CheckUpdates || checkingForUpdate || DateTime.UtcNow - Settings.LastUpdateCheck < TimeSpan.FromHours(24))
                return;
            checkingForUpdate = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, which GitHub requires
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Program.LatestReleaseApi);
                    request.UserAgent = Program.Name + "/" + Program.Version;
                    request.Timeout = request.ReadWriteTimeout = 10000;
                    using (WebResponse response = request.GetResponse())
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(response.GetResponseStream()))
                    {
                        Match match = Regex.Match(reader.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                        if (match.Success)
                            tag = match.Groups[1].Value;
                    }
                }
                catch (Exception) { }
                try
                {
                    invoker.BeginInvoke((Action)delegate
                    {
                        checkingForUpdate = false;
                        if (tag != null)
                            Settings.SaveUpdateCheck(DateTime.UtcNow, tag);
                        SettingsPanel sp = panel as SettingsPanel;
                        if (sp != null && !sp.IsDisposed)
                            sp.UpdateFound();
                    });
                }
                catch (Exception) { } // exiting
            });
        }

        public bool DownloadingUpdate { get; private set; }

        // Downloads the new version's setup from its GitHub release and runs it quietly: it closes
        // this copy, replaces the installed exe and starts the new one. If the download fails, the
        // release page opens instead.
        public void InstallUpdate()
        {
            string version = UpdateVersion;
            if (version == null || DownloadingUpdate)
                return;
            DownloadingUpdate = true;
            string url = string.Format("{0}/releases/download/v{1}/{2}-Setup.exe", Program.Url, version, Program.Name);
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Program.Name + "-Setup.exe");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                    using (WebClient client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.UserAgent] = Program.Name + "/" + Program.Version;
                        client.DownloadFile(url, file);
                    }
                    ok = true;
                }
                catch (Exception) { }
                try
                {
                    invoker.BeginInvoke((Action)delegate
                    {
                        DownloadingUpdate = false;
                        try
                        {
                            if (ok)
                                Process.Start(file, "--quiet");
                            else
                                Process.Start(Program.Url + "/releases/latest");
                        }
                        catch (Exception) { }
                        SettingsPanel sp = panel as SettingsPanel;
                        if (sp != null && !sp.IsDisposed)
                            sp.UpdateFound();
                    });
                }
                catch (Exception) { } // exiting
            });
        }

        // Light/dark mode, accent color, display scale or taskbar changes.
        void OnSystemChange(object sender, EventArgs e)
        {
            invoker.BeginInvoke((Action)delegate
            {
                Native.AllowDarkMenus();
                Theme = new Theme();
                Icons.Destroy(placeholder);
                placeholder = null;
                if (setupIcon != null)
                    setupIcon.Icon = Placeholder;
                foreach (PageIcon page in pages)
                {
                    page.View.ApplyTheme(Theme);
                    page.Refresh();
                }
            });
        }

        public void Exit()
        {
            SystemEvents.UserPreferenceChanged -= OnSystemChange;
            SystemEvents.DisplaySettingsChanged -= OnSystemChange;
            showRequests.Unregister(null);
            exitRequests.Unregister(null);
            if (panel != null)
                panel.Close();
            foreach (PageIcon page in pages)
                page.Dispose();
            pages.Clear();
            if (setupIcon != null)
                setupIcon.Dispose();
            ExitThread();
        }
    }

    // One page's tray icon (its favicon, or the globe) with its flyout.
    class PageIcon : IDisposable
    {
        public readonly Site Site;
        public readonly SiteView View;
        readonly TrayApp app;
        readonly NotifyIcon tray = new NotifyIcon();
        Icon favicon;

        public PageIcon(TrayApp app, Site site)
        {
            this.app = app;
            Site = site;
            View = new SiteView(app, site);
            tray.ContextMenu = new ContextMenu(new[]
            {
                new MenuItem("Open", delegate { View.Open(Cursor.Position); }) { DefaultItem = true },
                new MenuItem("Reload", delegate { View.Open(Cursor.Position); View.Reload(); }),
                new MenuItem("Open in browser", delegate { OpenInBrowser(); }),
                new MenuItem("Copy address", delegate { CopyAddress(); }),
                new MenuItem("-"),
                new MenuItem("Settings", delegate { app.ShowSettings(); }),
                new MenuItem("Exit", delegate { app.Exit(); }),
            });
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
            tray.MouseMove += delegate { View.Warm(); };
            Refresh();
            tray.Visible = true;
        }

        void Toggle()
        {
            if (View.Visible)
                View.Dismiss();
            else if (Environment.TickCount - View.ClosedAt > 300) // the click that closed it by deactivation
                View.Open(Cursor.Position);
        }

        void OpenInBrowser()
        {
            try { Process.Start(View.CurrentUrl); }
            catch (Exception) { }
        }

        void CopyAddress()
        {
            try { Clipboard.SetText(View.CurrentUrl); }
            catch (System.Runtime.InteropServices.ExternalException) { } // clipboard busy
        }

        // Re-renders the icon (new favicon, taskbar scale or theme) and the tooltip.
        public void Refresh()
        {
            Icon old = favicon;
            favicon = Icons.Favicon(Site.Favicon, Native.TrayIconSize());
            tray.Icon = favicon ?? app.Placeholder;
            Icons.Destroy(old);
            string tip = Site.Name;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        public void Dispose()
        {
            tray.Visible = false;
            tray.Dispose();
            View.Dispose();
            Icons.Destroy(favicon);
            favicon = null;
        }
    }
}
