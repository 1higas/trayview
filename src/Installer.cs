using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayView
{
    // TrayView-Setup.exe is TrayView.exe under another name: the word "setup" in the file name makes
    // it install itself instead of running. It installs for the current user only, so no admin
    // rights are needed: the exe goes to %LOCALAPPDATA%\Programs\TrayView, with a Start menu
    // shortcut and an entry in Settings > Apps to uninstall it. Running a newer setup updates it.
    static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.Id;
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Program.Name);
        public static readonly string Exe = Path.Combine(Dir, Program.Name + ".exe");
        static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.Name + ".lnk");

        public static bool IsSetup
        {
            get { return Path.GetFileName(Application.ExecutablePath).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        public static bool IsInstalled
        {
            get { return File.Exists(Exe); }
        }

        public static void Install(bool autostart)
        {
            StopRunningCopy();
            Directory.CreateDirectory(Dir);
            // Windows can hold on to the exe for a moment after its process ends.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(Application.ExecutablePath, Exe, true);
                    break;
                }
                catch (IOException)
                {
                    if (attempt == 20)
                        throw;
                    Thread.Sleep(250);
                }
            }

            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, Shortcut);
            Com(link, "TargetPath", BindingFlags.SetProperty, Exe);
            Com(link, "Description", BindingFlags.SetProperty, "Web pages in the notification area");
            Com(link, "Save", BindingFlags.InvokeMethod);

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Program.Name);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", "Thiago Fonseca");
                k.SetValue("URLInfoAbout", Program.Url);
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024 + 1), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Settings.SetAutostart(autostart, Exe);

            // A first install opens the onboarding; an update just comes back to the tray.
            bool configured = Settings.Load().Sites.Count > 0;
            Native.AllowSetForegroundWindow(-1);
            Process.Start(Exe, configured ? "--tray" : "");
        }

        // Started by Settings > Apps > Uninstall. Removes the app and everything it saved.
        public static void Uninstall()
        {
            StopRunningCopy();
            Settings.SetAutostart(false, null);
            File.Delete(Shortcut);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Settings.Delete();
            // The browser data, once its processes have exited (they follow the app within a moment).
            string data = Path.GetDirectoryName(Browser.DataFolder);
            for (int i = 0; i < 10 && Directory.Exists(data); i++)
            {
                try { Directory.Delete(data, true); }
                catch (IOException) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { Thread.Sleep(500); }
            }
            // This exe is the one being removed, so cmd deletes the folder once it has exited.
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"");
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            Process.Start(info);
        }

        // Asks a running TrayView (installed or not) to exit and waits until it has; an installed
        // copy that doesn't (an older version, or one that hangs) is ended.
        static void StopRunningCopy()
        {
            EventWaitHandle exit;
            if (EventWaitHandle.TryOpenExisting(Program.ExitEvent, out exit))
            {
                using (exit)
                    exit.Set();
                Mutex running;
                if (Mutex.TryOpenExisting(Program.RunningMutex, out running))
                    using (running)
                    {
                        try
                        {
                            if (running.WaitOne(5000))
                                running.ReleaseMutex();
                        }
                        catch (AbandonedMutexException) { } // it exited while holding it: fine
                    }
            }
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(Program.Name))
                using (p)
                {
                    if (p.Id == self)
                        continue;
                    try
                    {
                        if (string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase) && !p.WaitForExit(2000))
                        {
                            p.Kill();
                            p.WaitForExit(5000);
                        }
                    }
                    catch (Exception) { } // exited meanwhile, or not ours to inspect
                }
        }

        static object Com(object target, string member, BindingFlags kind, params object[] args)
        {
            return target.GetType().InvokeMember(member, kind, null, target, args);
        }
    }

    // The setup window: what gets installed where, Start with Windows, and Install.
    class SetupPanel : FluentPanel
    {
        readonly Toggle autostart;
        readonly TextItem error;
        readonly Buttons buttons;

        public SetupPanel() : base(new Theme())
        {
            Centered = true;
            ShowInTaskbar = true;
            CloseOnDeactivate = false;
            bool update = Installer.IsInstalled;
            Text = (update ? "Update " : "Install ") + Program.Name;

            TitleBar title = Add(new TitleBar(Text, "")); // ChromeClose
            title.Clicked += Dismiss;
            using (System.Drawing.Icon small = Native.AppIcon(Px(20)))
                title.Image = small == null ? null : small.ToBitmap();
            TextItem about = Add(new TextItem(update
                ? string.Format("Updates {0} to version {1}. Your pages and settings stay as they are.", Program.Name, Program.Version)
                : string.Format("Installs {0} {1} for your account and adds it to the Start menu. No administrator rights needed. "
                    + "To uninstall, use Settings > Apps.", Program.Name, Program.Version), Body));
            about.Tint = Theme.Secondary;
            autostart = Add(new Toggle("Start with Windows", !update || Settings.Autostart));
            autostart.Gap = 20;
            error = Add(new TextItem(null, Small));
            error.Tint = Theme.Error;
            error.Visible = false;
            buttons = Add(new Buttons(new[] { update ? "Update" : "Install", "Cancel" }, new Action[] { Install, Dismiss }));

            Relayout();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && buttons.Enabled)
                Install();
            else
                base.OnKeyDown(e);
        }

        void Install()
        {
            buttons.Enabled = false;
            Refresh();
            try
            {
                Installer.Install(autostart.On);
                Dismiss();
            }
            catch (Exception e)
            {
                error.Text = "Installation failed: " + e.Message;
                error.Visible = true;
                buttons.Enabled = true;
                Relayout();
            }
        }
    }
}
