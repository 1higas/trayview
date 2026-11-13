using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(TrayView.Program.Name)]
[assembly: AssemblyDescription("Web pages in the Windows notification area")]
[assembly: AssemblyProduct(TrayView.Program.Name)]
[assembly: AssemblyCopyright("Copyright (c) 2026 Thiago Fonseca. MIT License.")]
[assembly: AssemblyVersion(TrayView.Program.Version + ".0")]
[assembly: AssemblyFileVersion(TrayView.Program.Version + ".0")]

namespace TrayView
{
    static class Program
    {
        public const string Name = "TrayView", Id = "TrayView";
        public const string Version = "1.0.0";
        public const string Url = "https://github.com/1higas/trayview";
        public const string LatestReleaseApi = "https://api.github.com/repos/1higas/trayview/releases/latest";

        public const string RunningMutex = Id + ".Tray", ShowEvent = Id + ".Show", ExitEvent = Id + ".Exit";

        // TrayView.exe              tray icons; opens the onboarding when no page is set up yet. If
        //                           it is already running, opens that instance's settings (or onboarding).
        // TrayView.exe --tray       tray icons only (used when starting with Windows)
        // TrayView.exe --uninstall  removes the installed app and its data (Settings > Apps)
        // TrayView-Setup.exe        installs or updates the app (see Installer)
        // TrayView-Setup.exe --quiet  updates without asking (the app's own update)
        [STAThread]
        static int Main(string[] args)
        {
            bool trayOnly = args.Length == 1 && args[0] == "--tray";
            Native.AllowDarkMenus();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 1 && args[0] == "--uninstall")
            {
                Installer.Uninstall();
                return 0;
            }
            if (Installer.IsSetup && args.Length == 1 && args[0] == "--quiet")
            {
                // Started by the app's own update: no window, same Start with Windows choice.
                try { Installer.Install(Settings.Autostart || !Installer.IsInstalled); }
                catch (Exception e)
                {
                    MessageBox.Show("Could not update " + Name + ": " + e.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                return 0;
            }
            if (Installer.IsSetup)
            {
                SetupPanel setup = new SetupPanel();
                setup.Dismissed += Application.ExitThread;
                setup.Open(Cursor.Position);
                Application.Run();
                return 0;
            }

            bool first;
            using (Mutex running = new Mutex(true, RunningMutex, out first))
            using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent))
            using (EventWaitHandle exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEvent))
            {
                if (!first)
                {
                    if (!trayOnly)
                    {
                        Native.AllowSetForegroundWindow(-1); // ASFW_ANY: let the running copy take focus
                        show.Set();
                    }
                    return 0;
                }
                Application.Run(new TrayApp(!trayOnly, show, exit));
            }
            return 0;
        }
    }
}
