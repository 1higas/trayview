using System;
using System.Collections.Generic;
using System.Drawing;

namespace TrayView
{
    // Base for the app's own flyouts: the app icon in the title bar, sized for the current scale.
    class AppPanel : FluentPanel
    {
        protected readonly TrayApp App;
        protected TitleBar Title;

        public AppPanel(TrayApp app) : base(app.Theme)
        {
            App = app;
        }

        protected void LoadTitleIcon()
        {
            if (Title == null)
                return;
            if (Title.Image != null)
                Title.Image.Dispose();
            using (Icon small = Native.AppIcon(Px(20)))
                Title.Image = small == null ? null : small.ToBitmap();
        }

        protected override void Rescale()
        {
            LoadTitleIcon();
            base.Rescale();
        }

        protected static string Problem(string url)
        {
            return url == null ? "Enter a web address, such as https://example.com" : null;
        }
    }

    // Shown when no page is set up yet: asks for the address and whether to start with Windows.
    class OnboardingPanel : AppPanel
    {
        readonly TextBoxItem address;
        readonly TextItem error;
        readonly Toggle autostart;

        public OnboardingPanel(TrayApp app) : base(app)
        {
            Title = Add(new TitleBar("Welcome to " + Program.Name, null));
            LoadTitleIcon();
            TextItem intro = Add(new TextItem("Keep a web page, such as a smart home dashboard, one click away in the notification area. "
                + "Each page gets its own icon, taken from the site.", Body));
            intro.Tint = Theme.Secondary;
            TextItem label = Add(new TextItem("Page address", Strong));
            label.Gap = 20;
            address = Add(new TextBoxItem("https://example.com"));
            address.Gap = 8;
            error = Add(new TextItem(null, Small));
            error.Tint = Theme.Error;
            error.Gap = 6;
            autostart = Add(new Toggle("Start with Windows", true));
            autostart.Gap = 10;
            Buttons buttons = Add(new Buttons(new[] { "Open page" }, new Action[] { Submit }));
            buttons.Gap = 24;
            TextItem hint = Add(new TextItem("Right-click the icon later to add more pages or change settings.", Small));
            hint.Tint = Theme.Disabled;
            hint.Gap = 16;
            address.Submitted += Submit;
            Relayout();
        }

        protected override void OnOpened()
        {
            address.Box.Focus();
        }

        void Submit()
        {
            string url = Settings.NormalizeUrl(address.Text);
            error.Text = Problem(url);
            if (url == null)
            {
                Relayout();
                address.Box.Focus();
                return;
            }
            App.CompleteOnboarding(url, autostart.On);
        }
    }

    // Pages (add, change the address, remove) and app settings. Every change applies at once.
    class SettingsPanel : AppPanel
    {
        readonly Notice update;
        readonly TextItem pagesLabel, error;
        readonly TextBoxItem address;
        readonly Buttons addButtons, editButtons;
        readonly Toggle autostart;
        readonly List<SiteRow> rows = new List<SiteRow>();
        Site editing;

        public SettingsPanel(TrayApp app) : base(app)
        {
            Settings s = app.Settings;
            // On top: the panel is anchored by the tray, so it grows away from it and nothing moves.
            update = Add(new Notice());
            update.Link = "Update";
            Title = Add(new TitleBar(Program.Name + " settings", null));
            Title.Gap = 12;
            LoadTitleIcon();
            pagesLabel = Add(new TextItem("Pages", Strong));
            pagesLabel.Gap = 16;
            address = Add(new TextBoxItem("Add a page: https://example.com"));
            address.Gap = 12;
            error = Add(new TextItem(null, Small));
            error.Tint = Theme.Error;
            error.Gap = 6;
            addButtons = Add(new Buttons(new[] { "Add page" }, new Action[] { Submit }));
            addButtons.Gap = 6;
            editButtons = Add(new Buttons(new[] { "Save", "Cancel" }, new Action[] { Submit, CancelEdit }));
            editButtons.Gap = 6;
            Add(new Divider());

            Segmented unload = Add(new Segmented("Free memory after a page closes", Settings.UnloadLabels,
                Array.IndexOf(Settings.UnloadChoices, s.UnloadMinutes)));
            unload.Gap = 16;
            Toggle closeOnBlur = Add(new Toggle("Close pages when clicking elsewhere", s.CloseOnFocusLoss));
            closeOnBlur.Gap = 20;
            autostart = Add(new Toggle("Start with Windows", Settings.Autostart));
            autostart.Gap = 20;
            Toggle updates = Add(new Toggle("Check for updates", s.CheckUpdates));
            updates.Gap = 20;
            TextItem version = Add(new TextItem(Program.Name + " " + Program.Version, Small));
            version.Tint = Theme.Secondary;
            version.Gap = 20;

            address.Submitted += Submit;
            unload.Changed += i => App.Settings.SaveUnloadMinutes(Settings.UnloadChoices[i]);
            closeOnBlur.Changed += on => App.Settings.SaveCloseOnFocusLoss(on);
            autostart.Changed += on =>
            {
                App.SetAutostart(on);
                autostart.On = Settings.Autostart;
            };
            updates.Changed += on => App.SetCheckUpdates(on);
            update.Clicked += delegate
            {
                App.InstallUpdate();
                Sync();
            };
            App.Changed += Rebuild;
            Rebuild();
        }

        // Opens without a focused text box, like Windows' own settings.
        protected override void OnOpened()
        {
            ActiveControl = null;
            Focus();
        }

        // One row per page, between the "Pages" label and the address box.
        void Rebuild()
        {
            foreach (SiteRow row in rows)
                Remove(row);
            rows.Clear();
            int index = IndexOf(pagesLabel) + 1;
            foreach (Site site in App.Settings.Sites)
            {
                Site s = site;
                SiteRow row = Insert(index++, new SiteRow(s, Icons.Decode(s.Favicon)));
                row.Gap = rows.Count == 0 ? 8 : 4;
                row.Clicked += () => BeginEdit(s);
                row.Removed += () =>
                {
                    if (editing == s)
                        CancelEdit();
                    App.RemoveSite(s);
                };
                rows.Add(row);
            }
            if (editing != null && !App.Settings.Sites.Contains(editing))
                editing = null;
            Sync();
        }

        void BeginEdit(Site site)
        {
            editing = site;
            address.Text = site.Url;
            error.Text = null;
            Sync();
            address.Box.Focus();
            address.Box.SelectAll();
        }

        void CancelEdit()
        {
            editing = null;
            address.Text = "";
            error.Text = null;
            Sync();
        }

        void Submit()
        {
            string url = Settings.NormalizeUrl(address.Text);
            error.Text = Problem(url);
            if (url != null)
            {
                if (editing != null)
                    App.ChangeSiteUrl(editing, url);
                else
                    App.AddSite(url, true);
                editing = null;
                address.Text = "";
            }
            Sync();
        }

        void Sync()
        {
            pagesLabel.Text = rows.Count == 0 ? "No pages yet" : "Pages";
            addButtons.Visible = editing == null;
            editButtons.Visible = editing != null;
            foreach (SiteRow row in rows)
                row.Selected = row.Site == editing;
            update.Visible = App.UpdateVersion != null;
            update.Text = App.DownloadingUpdate ? "Downloading " + Program.Name + " " + App.UpdateVersion + "..."
                : Program.Name + " " + App.UpdateVersion + " is available";
            update.Link = App.DownloadingUpdate ? "" : "Update";
            Relayout();
        }

        public void UpdateFound()
        {
            Sync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                App.Changed -= Rebuild;
            base.Dispose(disposing);
        }
    }
}
