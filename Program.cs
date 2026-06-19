using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GfnFullscreen;

static class Program
{
    const string GfnProcessName = "GeForceNOW";
    const string GfnRelativePath = @"NVIDIA Corporation\GeForceNOW\CEF\GeForceNOW.exe";
    const string GfnJsonRelativePath = @"NVIDIA Corporation\GeForceNOW\CEF\GeForceNOW.json";
    const string RunKeyName = "GfnFullscreen";

    [STAThread]
    static int Main(string[] args)
    {
        bool install = args.Contains("--install");
        bool uninstall = args.Contains("--uninstall");

        if (install)
        {
            SetAutoStart(true);
            return 0;
        }

        if (uninstall)
        {
            SetAutoStart(false);
            return 0;
        }

        using var mutex = new Mutex(true, @"Global\GfnFullscreen_SingleInstance", out bool created);
        if (!created)
            return 0;

        EnsureNativeTitleBar();

        var gfnPath = ResolveGfnPath();
        if (gfnPath == null)
            return 1;

        // If GFN is already running, strip its titlebar and monitor
        var existingProc = FindGfnProcess();
        if (existingProc != null && existingProc.MainWindowHandle != IntPtr.Zero)
        {
            StripTitleBar(existingProc.MainWindowHandle);
            MonitorGfn();
            return 0;
        }

        // Show splash FIRST, launch GFN after splash is visible
        var splashPath = FindSplashPath();
        if (splashPath != null)
        {
            ShowSplashThenLaunchGfn(splashPath, gfnPath);
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true });
            WaitAndStripTitleBar();
        }

        MonitorGfn();

        return 0;
    }

    static void ShowSplashThenLaunchGfn(string splashPath, string gfnPath)
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Topmost = true,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };

        // Decode GIF frames and per-frame delays
        var decoder = new GifBitmapDecoder(
            new Uri(splashPath),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frames = decoder.Frames;
        var delays = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            frames[i].Freeze();
            delays[i] = 100;
            if (frames[i].Metadata is BitmapMetadata meta)
            {
                try
                {
                    var d = meta.GetQuery("/grctlext/Delay");
                    if (d is ushort val && val > 0)
                        delays[i] = val * 10;
                }
                catch { }
            }
        }

        var image = new Image
        {
            Source = frames[0],
            Stretch = Stretch.Uniform
        };

        var fadeOverlay = new Border { Background = Brushes.Black, Opacity = 0 };

        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(fadeOverlay);
        window.Content = grid;

        bool gfnLaunched = false;
        IntPtr gfnHwnd = IntPtr.Zero;

        void FadeOutAndHandoff()
        {
            const int fadeDurationMs = 800;
            const int stepMs = 16;
            double step = (double)stepMs / fadeDurationMs;

            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(stepMs) };
            fadeTimer.Tick += (_, _) =>
            {
                fadeOverlay.Opacity = Math.Min(fadeOverlay.Opacity + step, 1.0);
                if (fadeOverlay.Opacity >= 1.0)
                {
                    fadeTimer.Stop();
                    StripTitleBar(gfnHwnd);
                    LockSetForegroundWindow(LSFW_UNLOCK);
                    SetForegroundWindow(gfnHwnd);
                    window.Close();
                }
            };
            fadeTimer.Start();
        }

        void OnSplashEnded()
        {
            if (gfnHwnd != IntPtr.Zero)
            {
                FadeOutAndHandoff();
            }
            else
            {
                Task.Run(() =>
                {
                    for (int i = 0; i < 40; i++)
                    {
                        Thread.Sleep(250);
                        var proc = FindGfnProcess();
                        if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            gfnHwnd = proc.MainWindowHandle;
                            app.Dispatcher.Invoke(FadeOutAndHandoff);
                            return;
                        }
                    }
                    app.Dispatcher.Invoke(() => window.Close());
                });
            }
        }

        window.ContentRendered += (_, _) =>
        {
            LockSetForegroundWindow(LSFW_LOCK);

            // Animate GIF: cycle through frames with per-frame delays
            int frameIndex = 0;
            var animTimer = new DispatcherTimer();
            animTimer.Interval = TimeSpan.FromMilliseconds(delays[0]);
            animTimer.Tick += (_, _) =>
            {
                frameIndex++;
                if (frameIndex >= frames.Count)
                {
                    animTimer.Stop();
                    OnSplashEnded();
                    return;
                }
                image.Source = frames[frameIndex];
                animTimer.Interval = TimeSpan.FromMilliseconds(delays[frameIndex]);
            };
            animTimer.Start();

            // Launch GFN AFTER splash is visible
            if (!gfnLaunched)
            {
                gfnLaunched = true;
                Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true });
            }

            // Detect GFN window handle (runs behind Topmost splash, not hidden)
            var detectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            detectTimer.Tick += (_, _) =>
            {
                if (gfnHwnd != IntPtr.Zero) return;

                var proc = FindGfnProcess();
                if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                {
                    gfnHwnd = proc.MainWindowHandle;
                    detectTimer.Stop();
                }
            };
            detectTimer.Start();
        };

        window.Closed += (_, _) =>
        {
            LockSetForegroundWindow(LSFW_UNLOCK);
            app.Shutdown();
        };

        app.Run(window);
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static bool IsGfnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.Equals(GfnProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void EnsureAllGfnStripped()
    {
        var procs = Process.GetProcessesByName(GfnProcessName);
        foreach (var proc in procs)
        {
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                long style = GetWindowLongPtr(proc.MainWindowHandle, GWL_STYLE);
                if ((style & WS_CAPTION) != 0)
                    StripTitleBar(proc.MainWindowHandle);
            }
            proc.Dispose();
        }
    }

    static void MonitorGfn()
    {
        uint threadId = GetCurrentThreadId();

        WinEventDelegate callback = (hook, eventType, hwnd, idObject, idChild, eventThread, eventTime) =>
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW) return;
            if (!IsGfnWindow(hwnd)) return;

            long style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CAPTION) != 0)
                StripTitleBar(hwnd);
        };

        var pinned = GCHandle.Alloc(callback);

        var hook1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var hook2 = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var hook3 = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        SetTimer(IntPtr.Zero, UIntPtr.Zero, 500, IntPtr.Zero);

        Task.Run(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                var procs = Process.GetProcessesByName(GfnProcessName);
                if (procs.Length == 0) break;
                foreach (var p in procs) p.Dispose();
            }
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_TIMER)
                EnsureAllGfnStripped();

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWinEvent(hook1);
        UnhookWinEvent(hook2);
        UnhookWinEvent(hook3);
        pinned.Free();
    }

    static void WaitAndStripTitleBar()
    {
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            var proc = FindGfnProcess();
            if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
            {
                StripTitleBar(proc.MainWindowHandle);
                return;
            }
        }
    }

    static void StripTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU |
                   WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, SWP_FRAMECHANGED | SWP_NOZORDER);
    }

    static void EnsureNativeTitleBar()
    {
        var jsonPath = ResolveGfnJsonPath();
        if (jsonPath == null) return;

        try
        {
            var content = File.ReadAllText(jsonPath);
            var replaced = content.Replace(
                "nv-custom-black-window=true",
                "nv-custom-black-window=false");
            if (replaced != content)
                File.WriteAllText(jsonPath, replaced);
        }
        catch { }
    }

    static string? ResolveGfnJsonPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, GfnJsonRelativePath);
        return File.Exists(path) ? path : null;
    }

    static Process? FindGfnProcess()
    {
        var procs = Process.GetProcessesByName(GfnProcessName);
        foreach (var p in procs)
        {
            if (p.MainWindowHandle != IntPtr.Zero)
                return p;
        }
        return procs.Length > 0 ? procs[0] : null;
    }

    static string? ResolveGfnPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return File.Exists(Path.Combine(localAppData, GfnRelativePath))
            ? Path.Combine(localAppData, GfnRelativePath)
            : null;
    }

    static string? FindSplashPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "nvidia.gif");
        return File.Exists(path) ? path : null;
    }

    static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null) return;

        if (enable)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null)
                key.SetValue(RunKeyName, $"\"{exe}\" --launch");
        }
        else
        {
            key.DeleteValue(RunKeyName, false);
        }
    }

    // ── Win32 Interop ───────────────────────────────────────────

    const int GWL_STYLE = -16;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_THICKFRAME = 0x00040000L;
    const long WS_SYSMENU = 0x00080000L;
    const long WS_MINIMIZEBOX = 0x00020000L;
    const long WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_MAXIMIZE = 0x01000000L;
    const long WS_POPUP = 0x80000000L;

    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool LockSetForegroundWindow(uint uLockCode);
    const uint LSFW_LOCK = 1;
    const uint LSFW_UNLOCK = 2;

    // ── Event Hook Interop ──────────────────────────────────────

    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint WM_QUIT = 0x0012;
    const uint WM_TIMER = 0x0113;
    const int OBJID_WINDOW = 0;

    [DllImport("user32.dll")]
    static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
}
