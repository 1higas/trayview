using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace TrayView
{
    // The callbacks WebView2 calls. Only Invoke is declared; the CLR provides IUnknown.
    [ComImport, Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnvironmentCompletedHandler { [PreserveSig] int Invoke(int errorCode, IntPtr result); }

    [ComImport, Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IControllerCompletedHandler { [PreserveSig] int Invoke(int errorCode, IntPtr result); }

    [ComImport, Guid("a2508329-7da8-49d7-8c05-fa125e4aee8d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGetFaviconCompletedHandler { [PreserveSig] int Invoke(int errorCode, IntPtr result); }

    [ComImport, Guid("2913da94-833d-4de0-8dca-900fc524a1a4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFaviconChangedEventHandler { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }

    [ComImport, Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INewWindowRequestedEventHandler { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }

    [ComImport, Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDocumentTitleChangedEventHandler { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }

    [ComImport, Guid("b29c7e28-fa79-41a8-8e44-65811c76dcb2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAcceleratorKeyPressedEventHandler { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }

    [ComImport, Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INavigationCompletedEventHandler { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }

    class Completion : IEnvironmentCompletedHandler, IControllerCompletedHandler, IGetFaviconCompletedHandler
    {
        readonly Action<int, IntPtr> callback;
        public Completion(Action<int, IntPtr> callback) { this.callback = callback; }
        public int Invoke(int errorCode, IntPtr result)
        {
            try { callback(errorCode, result); }
            catch (Exception) { }
            return 0;
        }
    }

    class EventCallback : IFaviconChangedEventHandler, INewWindowRequestedEventHandler, IDocumentTitleChangedEventHandler, IAcceleratorKeyPressedEventHandler, INavigationCompletedEventHandler
    {
        readonly Action<IntPtr, IntPtr> callback;
        public EventCallback(Action<IntPtr, IntPtr> callback) { this.callback = callback; }
        public int Invoke(IntPtr sender, IntPtr args)
        {
            try { callback(sender, args); }
            catch (Exception) { }
            return 0;
        }
    }

    // A WebView2 browser hosted in a window, driven through its COM interfaces directly.
    //
    // No WebView2 SDK files are needed: the runtime that ships with Windows 11 is located in the
    // registry and its own client DLL creates the environment (the same entry point the SDK's
    // WebView2Loader calls). Methods are called by vtable slot, numbered as in WebView2.h
    // (0-2 are IUnknown). All browsers share one environment, so one set of browser processes;
    // when the last browser closes the environment is released and those processes exit.
    sealed class Browser
    {
        const string RuntimeId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        static readonly Guid IID_ICoreWebView2Controller2 = new Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab");
        static readonly Guid IID_ICoreWebView2_15 = new Guid("517b2d1d-7dae-4a66-a4f4-10352ffb9518");
        static readonly Guid IID_ICoreWebView2_19 = new Guid("6921f954-79b0-437f-a997-c85811897c68");

        // ICoreWebView2Environment
        const int CreateCoreWebView2Controller = 3;
        // ICoreWebView2Controller(2)
        const int PutIsVisible = 4, PutBounds = 6, MoveFocus = 12, AddAcceleratorKeyPressed = 19;
        const int NotifyParentWindowPositionChanged = 23, Close_ = 24, GetCoreWebView2 = 25, PutDefaultBackgroundColor = 27;
        // ICoreWebView2(_15, _19)
        const int GetSettings = 3, GetSource = 4, Navigate_ = 5, AddNavigationCompleted = 15, Reload_ = 31, GetBrowserProcessId = 37, AddNewWindowRequested = 44;
        const int AddDocumentTitleChanged = 46, GetDocumentTitle = 48, AddFaviconChanged = 109, GetFavicon = 112;
        const int PutMemoryUsageTargetLevel = 120;
        // ICoreWebView2Settings
        const int PutIsStatusBarEnabled = 10;
        // ICoreWebView2AcceleratorKeyPressedEventArgs / ICoreWebView2NewWindowRequestedEventArgs
        const int ArgsGetKeyEventKind = 3, ArgsGetVirtualKey = 4, ArgsPutKeyHandled = 8;
        const int ArgsGetUri = 3, ArgsPutWindowHandled = 6;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Fn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnInt(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutInt(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutPtr(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnString(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnRect(IntPtr self, RECT value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnPtrPtr(IntPtr self, IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnIntPtr(IntPtr self, int a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnAddHandler(IntPtr self, IntPtr handler, out long token);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateEnvironmentFn(int checkRunningInstance, int runtimeType, [MarshalAs(UnmanagedType.LPWStr)] string userDataFolder, IntPtr options, IntPtr handler);

        static CreateEnvironmentFn createEnvironment;
        static IntPtr environment;
        static bool creatingEnvironment;
        static int users;
        static readonly List<Action<string>> waiting = new List<Action<string>>();

        IntPtr controller, webview;

        public event Action<string> TitleChanged;
        public event Action<byte[]> FaviconChanged;
        public event Action<string> NewWindowRequested;
        public event Action EscapePressed;
        public event Action NavigationCompleted; // also on failure: the page shows its error then

        public static string DataFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.Id, "WebView2"); }
        }

        // Creates a browser inside the window; done gets the browser, or null and an error message.
        public static void Create(IntPtr parent, Color background, Action<Browser, string> done)
        {
            users++; // keeps the environment alive while this browser is being created or is open
            WithEnvironment(error =>
            {
                if (error != null)
                {
                    ReleaseUser();
                    done(null, error);
                    return;
                }
                IntPtr handler = Marshal.GetComInterfaceForObject(new Completion((hr, controller) =>
                {
                    if (hr < 0 || controller == IntPtr.Zero)
                    {
                        ReleaseUser();
                        done(null, Describe(hr));
                        return;
                    }
                    Marshal.AddRef(controller);
                    done(new Browser(controller, background), null);
                }), typeof(IControllerCompletedHandler));
                int result = Slot<FnPtrPtr>(environment, CreateCoreWebView2Controller)(environment, parent, handler);
                Marshal.Release(handler);
                if (result < 0)
                {
                    ReleaseUser();
                    done(null, Describe(result));
                }
            });
        }

        Browser(IntPtr controller, Color background)
        {
            this.controller = controller;
            Slot<FnOutPtr>(controller, GetCoreWebView2)(controller, out webview);

            IntPtr controller2 = Query(controller, IID_ICoreWebView2Controller2);
            if (controller2 != IntPtr.Zero)
            {
                // COREWEBVIEW2_COLOR is { A, R, G, B } in memory.
                Slot<FnInt>(controller2, PutDefaultBackgroundColor)(controller2, 255 | background.R << 8 | background.G << 16 | background.B << 24);
                Marshal.Release(controller2);
            }
            IntPtr settings;
            if (Slot<FnOutPtr>(webview, GetSettings)(webview, out settings) == 0)
            {
                Slot<FnInt>(settings, PutIsStatusBarEnabled)(settings, 0);
                Marshal.Release(settings);
            }

            AddHandler(controller, AddAcceleratorKeyPressed, typeof(IAcceleratorKeyPressedEventHandler), OnAcceleratorKey);
            AddHandler(webview, AddNewWindowRequested, typeof(INewWindowRequestedEventHandler), OnNewWindow);
            AddHandler(webview, AddDocumentTitleChanged, typeof(IDocumentTitleChangedEventHandler), OnTitleChanged);
            AddHandler(webview, AddNavigationCompleted, typeof(INavigationCompletedEventHandler), delegate
            {
                if (NavigationCompleted != null)
                    NavigationCompleted();
            });
            IntPtr webview15 = Query(webview, IID_ICoreWebView2_15);
            if (webview15 != IntPtr.Zero)
            {
                AddHandler(webview15, AddFaviconChanged, typeof(IFaviconChangedEventHandler), OnFaviconChanged);
                Marshal.Release(webview15);
            }
        }

        public void Navigate(string url)
        {
            Slot<FnString>(webview, Navigate_)(webview, url);
        }

        public void Reload()
        {
            Slot<Fn>(webview, Reload_)(webview);
        }

        public bool Visible
        {
            set { Slot<FnInt>(controller, PutIsVisible)(controller, value ? 1 : 0); }
        }

        public Rectangle Bounds
        {
            set
            {
                RECT r = new RECT { Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom };
                Slot<FnRect>(controller, PutBounds)(controller, r);
            }
        }

        public void Focus()
        {
            Slot<FnInt>(controller, MoveFocus)(controller, 0); // COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC
        }

        public void ParentMoved()
        {
            Slot<Fn>(controller, NotifyParentWindowPositionChanged)(controller);
        }

        // Low lets the browser trim its memory while hidden; Normal when shown again.
        public bool LowMemory
        {
            set
            {
                IntPtr webview19 = Query(webview, IID_ICoreWebView2_19);
                if (webview19 == IntPtr.Zero)
                    return;
                Slot<FnInt>(webview19, PutMemoryUsageTargetLevel)(webview19, value ? 1 : 0);
                Marshal.Release(webview19);
            }
        }

        // The address of the page shown now, which may differ from the one first opened.
        public string Source
        {
            get { return GetString(webview, GetSource); }
        }

        public int ProcessId
        {
            get
            {
                int id;
                return Slot<FnOutInt>(webview, GetBrowserProcessId)(webview, out id) == 0 ? id : 0;
            }
        }

        // Destroys the browser. The browser processes exit once no browser is left.
        public void Close()
        {
            if (controller == IntPtr.Zero)
                return;
            Slot<Fn>(controller, Close_)(controller);
            Marshal.Release(webview);
            Marshal.Release(controller);
            webview = controller = IntPtr.Zero;
            ReleaseUser();
        }

        void OnAcceleratorKey(IntPtr sender, IntPtr args)
        {
            int kind, key;
            Slot<FnOutInt>(args, ArgsGetKeyEventKind)(args, out kind);
            Slot<FnOutInt>(args, ArgsGetVirtualKey)(args, out key);
            if (kind == 0 && key == 0x1B) // COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN, VK_ESCAPE
            {
                Slot<FnInt>(args, ArgsPutKeyHandled)(args, 1);
                if (EscapePressed != null)
                    EscapePressed();
            }
        }

        // Links that would open a new window go to the default browser instead.
        void OnNewWindow(IntPtr sender, IntPtr args)
        {
            string uri = GetString(args, ArgsGetUri);
            Slot<FnInt>(args, ArgsPutWindowHandled)(args, 1);
            if (uri != null && NewWindowRequested != null)
                NewWindowRequested(uri);
        }

        void OnTitleChanged(IntPtr sender, IntPtr args)
        {
            string title = GetString(webview, GetDocumentTitle);
            if (TitleChanged != null)
                TitleChanged(title);
        }

        void OnFaviconChanged(IntPtr sender, IntPtr args)
        {
            RequestFavicon();
        }

        // Asks for the current page's favicon; FaviconChanged reports it if there is one.
        public void RequestFavicon()
        {
            if (webview == IntPtr.Zero)
                return;
            IntPtr webview15 = Query(webview, IID_ICoreWebView2_15);
            if (webview15 == IntPtr.Zero)
                return;
            IntPtr handler = Marshal.GetComInterfaceForObject(new Completion((hr, stream) =>
            {
                if (hr < 0 || stream == IntPtr.Zero)
                    return;
                byte[] png = ReadStream(stream);
                if (png.Length > 0 && FaviconChanged != null)
                    FaviconChanged(png);
            }), typeof(IGetFaviconCompletedHandler));
            Slot<FnIntPtr>(webview15, GetFavicon)(webview15, 0, handler); // COREWEBVIEW2_FAVICON_IMAGE_FORMAT_PNG
            Marshal.Release(handler);
            Marshal.Release(webview15);
        }

        static byte[] ReadStream(IntPtr pointer)
        {
            IStream stream = (IStream)Marshal.GetObjectForIUnknown(pointer);
            MemoryStream result = new MemoryStream();
            IntPtr read = Marshal.AllocHGlobal(4);
            try
            {
                stream.Seek(0, 0, IntPtr.Zero);
                byte[] buffer = new byte[16384];
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    int n = Marshal.ReadInt32(read);
                    if (n <= 0)
                        break;
                    result.Write(buffer, 0, n);
                }
            }
            catch (COMException) { }
            finally
            {
                Marshal.FreeHGlobal(read);
                Marshal.ReleaseComObject(stream);
            }
            return result.ToArray();
        }

        static void WithEnvironment(Action<string> ready)
        {
            if (environment != IntPtr.Zero)
            {
                ready(null);
                return;
            }
            waiting.Add(ready);
            if (creatingEnvironment)
                return;
            creatingEnvironment = true;
            string error = null;
            try
            {
                IntPtr handler = Marshal.GetComInterfaceForObject(new Completion((hr, env) =>
                {
                    creatingEnvironment = false;
                    if (hr >= 0 && env != IntPtr.Zero)
                    {
                        Marshal.AddRef(env);
                        environment = env;
                    }
                    EnvironmentReady(environment != IntPtr.Zero ? null : Describe(hr));
                }), typeof(IEnvironmentCompletedHandler));
                int result = LoadRuntime()(1, 0, DataFolder, IntPtr.Zero, handler);
                Marshal.Release(handler);
                if (result < 0)
                    error = Describe(result);
            }
            catch (Exception e) { error = e.Message; }
            if (error != null)
            {
                creatingEnvironment = false;
                EnvironmentReady(error);
            }
        }

        static void EnvironmentReady(string error)
        {
            Action<string>[] ready = waiting.ToArray();
            waiting.Clear();
            foreach (Action<string> action in ready)
                action(error);
            if (users == 0)
                ReleaseEnvironment();
        }

        static void ReleaseUser()
        {
            if (--users == 0)
                ReleaseEnvironment();
        }

        static void ReleaseEnvironment()
        {
            if (environment == IntPtr.Zero || creatingEnvironment)
                return;
            Marshal.Release(environment);
            environment = IntPtr.Zero;
        }

        static CreateEnvironmentFn LoadRuntime()
        {
            if (createEnvironment != null)
                return createEnvironment;
            string dll = FindRuntime();
            if (dll == null)
                throw new InvalidOperationException("The Microsoft Edge WebView2 Runtime is not installed. Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            IntPtr module = Native.LoadLibraryExW(dll, IntPtr.Zero, 8); // LOAD_WITH_ALTERED_SEARCH_PATH
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr create = Native.GetProcAddress(module, "CreateWebViewEnvironmentWithOptionsInternal");
            if (create == IntPtr.Zero)
                throw new InvalidOperationException("This version of the WebView2 Runtime is not supported.");
            createEnvironment = (CreateEnvironmentFn)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateEnvironmentFn));
            return createEnvironment;
        }

        // The runtime's install folder is recorded by its updater, per machine or per user.
        static string FindRuntime()
        {
            string arch = IntPtr.Size == 4 ? "x86"
                : string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = { RegistryView.Registry32, RegistryView.Registry64, RegistryView.Default };
            for (int i = 0; i < hives.Length; i++)
                using (RegistryKey root = RegistryKey.OpenBaseKey(hives[i], views[i]))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\ClientState\" + RuntimeId))
                {
                    string folder = key == null ? null : key.GetValue("EBWebView") as string;
                    if (string.IsNullOrEmpty(folder))
                        continue;
                    string path = Path.Combine(folder, "EBWebView", arch, "EmbeddedBrowserWebView.dll");
                    if (File.Exists(path))
                        return path;
                }
            return null;
        }

        static string Describe(int hr)
        {
            return string.Format("The browser could not start (error 0x{0:X8}).", hr);
        }

        static void AddHandler(IntPtr target, int slot, Type handlerType, Action<IntPtr, IntPtr> callback)
        {
            IntPtr handler = Marshal.GetComInterfaceForObject(new EventCallback(callback), handlerType);
            long token;
            Slot<FnAddHandler>(target, slot)(target, handler, out token);
            Marshal.Release(handler);
        }

        static string GetString(IntPtr target, int slot)
        {
            IntPtr text;
            if (Slot<FnOutPtr>(target, slot)(target, out text) != 0 || text == IntPtr.Zero)
                return null;
            string value = Marshal.PtrToStringUni(text);
            Marshal.FreeCoTaskMem(text);
            return value;
        }

        static IntPtr Query(IntPtr target, Guid iid)
        {
            IntPtr result;
            return Marshal.QueryInterface(target, ref iid, out result) == 0 ? result : IntPtr.Zero;
        }

        static T Slot<T>(IntPtr target, int index) where T : class
        {
            IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(target), index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(method, typeof(T)) as T;
        }
    }
}
