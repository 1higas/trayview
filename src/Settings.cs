using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayView
{
    // A page pinned to the tray. Its size is in device-independent pixels (96 DPI), so it looks the
    // same at any display scale.
    class Site
    {
        public const int DefaultWidth = 400, DefaultHeight = 800, MinWidth = 320, MinHeight = 360;

        public string Id, Url, Title;
        public int Width = DefaultWidth, Height = DefaultHeight, Order;
        public byte[] Favicon; // PNG, as WebView2 hands it over

        public string Host
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(Url, UriKind.Absolute, out uri) ? uri.Host : Url;
            }
        }

        public string Name { get { return string.IsNullOrEmpty(Title) ? Host : Title; } }
    }

    // Everything is kept under HKCU\Software\TrayView; each page is a subkey of Sites.
    class Settings
    {
        const string KeyPath = @"Software\" + Program.Id, SitesPath = KeyPath + @"\Sites";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // "Free memory after a page closes": minutes, 0 = right away, -1 = never.
        public static readonly int[] UnloadChoices = { 0, 1, 5, 30, -1 };
        public static readonly string[] UnloadLabels = { "Now", "1 min", "5 min", "30 min", "Never" };

        public readonly List<Site> Sites = new List<Site>();
        public int UnloadMinutes = 5;
        public bool CloseOnFocusLoss = true;
        public bool CheckUpdates = true;
        public DateTime LastUpdateCheck;
        public string LatestVersion;

        public static Settings Load()
        {
            Settings s = new Settings();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null)
                    return s;
                s.UnloadMinutes = (int)key.GetValue("UnloadMinutes", 5);
                if (Array.IndexOf(UnloadChoices, s.UnloadMinutes) < 0)
                    s.UnloadMinutes = 5;
                s.CloseOnFocusLoss = (int)key.GetValue("CloseOnFocusLoss", 1) != 0;
                s.CheckUpdates = (int)key.GetValue("CheckUpdates", 1) != 0;
                s.LastUpdateCheck = new DateTime((long)key.GetValue("LastUpdateCheck", 0L), DateTimeKind.Utc);
                s.LatestVersion = key.GetValue("LatestVersion") as string;
            }
            using (RegistryKey sites = Registry.CurrentUser.OpenSubKey(SitesPath))
                if (sites != null)
                    foreach (string id in sites.GetSubKeyNames())
                        using (RegistryKey k = sites.OpenSubKey(id))
                        {
                            string url = k.GetValue("Url") as string;
                            if (url == null)
                                continue;
                            Site site = new Site();
                            site.Id = id;
                            site.Url = url;
                            site.Title = k.GetValue("Title") as string;
                            site.Width = Math.Max(Site.MinWidth, (int)k.GetValue("Width", Site.DefaultWidth));
                            site.Height = Math.Max(Site.MinHeight, (int)k.GetValue("Height", Site.DefaultHeight));
                            site.Order = (int)k.GetValue("Order", 0);
                            site.Favicon = k.GetValue("Favicon") as byte[];
                            s.Sites.Add(site);
                        }
            s.Sites.Sort((a, b) => a.Order.CompareTo(b.Order));
            return s;
        }

        public Site NewSite(string url)
        {
            Site site = new Site();
            site.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            site.Url = url;
            foreach (Site other in Sites)
                site.Order = Math.Max(site.Order, other.Order + 1);
            Sites.Add(site);
            SaveSite(site);
            return site;
        }

        public void SaveSite(Site site)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SitesPath + @"\" + site.Id))
            {
                k.SetValue("Url", site.Url);
                k.SetValue("Width", site.Width, RegistryValueKind.DWord);
                k.SetValue("Height", site.Height, RegistryValueKind.DWord);
                k.SetValue("Order", site.Order, RegistryValueKind.DWord);
                if (site.Title != null)
                    k.SetValue("Title", site.Title);
                else
                    k.DeleteValue("Title", false);
                if (site.Favicon != null)
                    k.SetValue("Favicon", site.Favicon, RegistryValueKind.Binary);
                else
                    k.DeleteValue("Favicon", false);
            }
        }

        public void DeleteSite(Site site)
        {
            Sites.Remove(site);
            Registry.CurrentUser.DeleteSubKeyTree(SitesPath + @"\" + site.Id, false);
        }

        public void SaveUnloadMinutes(int minutes)
        {
            UnloadMinutes = minutes;
            Save("UnloadMinutes", minutes);
        }

        public void SaveCloseOnFocusLoss(bool on)
        {
            CloseOnFocusLoss = on;
            Save("CloseOnFocusLoss", on ? 1 : 0);
        }

        public void SaveCheckUpdates(bool on)
        {
            CheckUpdates = on;
            Save("CheckUpdates", on ? 1 : 0);
        }

        public void SaveUpdateCheck(DateTime when, string latest)
        {
            LastUpdateCheck = when;
            LatestVersion = latest;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue("LastUpdateCheck", when.Ticks, RegistryValueKind.QWord);
                key.SetValue("LatestVersion", latest);
            }
        }

        static void Save(string name, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value, RegistryValueKind.DWord);
        }

        // Start with Windows: a per-user Run value that starts the app in the tray only.
        public static bool Autostart
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath))
                    return key != null && key.GetValue(Program.Id) != null;
            }
            set { SetAutostart(value, Application.ExecutablePath); }
        }

        public static void SetAutostart(bool on, string exe)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (on)
                    key.SetValue(Program.Id, "\"" + exe + "\" --tray");
                else
                    key.DeleteValue(Program.Id, false);
            }
        }

        // Removes every setting and page (when uninstalling).
        public static void Delete()
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        }

        // Accepts "example.com" or a full address; returns null unless it is an http(s) address.
        public static string NormalizeUrl(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return null;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
                text = "https://" + text;
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
                return null;
            return uri.AbsoluteUri;
        }
    }
}
