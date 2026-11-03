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
    const string LaunchEventName = @"Local\GfnFullscreen_Launch";

    // How long to keep watching after GFN disappears (covers updater restarts).
    static readonly TimeSpan MonitorGraceAfterExit = TimeSpan.FromMinutes(3);

    [STAThread]
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GfnFullscreen could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    static int Run(string[] args)
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

        using var launchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, LaunchEventName);
        using var mutex = new Mutex(true, @"Local\GfnFullscreen_SingleInstance", out bool created);
        if (!created)
        {
            launchEvent.Set();
            return 0;
        }

        // Apply update-reset settings before starting GFN, never interrupt a session.
        if (!IsGfnRunning())
            EnsureNativeTitleBar();

        var gfnPath = ResolveGfnPath();
        if (gfnPath == null)
        {
            MessageBox.Show("GeForce NOW was not found in its default installation folder. Check that it is installed for this Windows user.", "GfnFullscreen", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        var existingHwnd = FindGfnMainHwnd();
        if (existingHwnd != IntPtr.Zero)
        {
            if (!ShowMpvSplash(gfnPath, launchGfn: false))
            {
                if (IsIconic(existingHwnd)) ShowWindow(existingHwnd, SW_RESTORE);
                ForceFullscreen(existingHwnd);
                SetForegroundWindow(existingHwnd);
            }
            MonitorGfn(launchEvent, gfnPath);
            return 0;
        }

        var splashPath = FindSplashPath();
        if (ShowMpvSplash(gfnPath, launchGfn: true))
        {
            // MPV handled the splash and launch.
        }
        else if (splashPath != null)
        {
            ShowSplashThenLaunchGfn(splashPath, gfnPath);
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true });
            WaitAndForceFullscreen();
        }

        MonitorGfn(launchEvent, gfnPath);
        return 0;
    }

    // Hold the final black frame until both the animation and app startup finish.
    static bool ShowMpvSplash(string gfnPath, bool launchGfn)
    {
        string mpvPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MPV Player", "mpv.exe");
        string videoPath = Path.Combine(AppContext.BaseDirectory, "nvidia-splash.mp4");
        if (!File.Exists(mpvPath) || !File.Exists(videoPath)) return false;

        Process? player = null;
        bool startedGfn = false;
        try
        {
            var options = new ProcessStartInfo { FileName = mpvPath, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--no-config", "--fullscreen", "--ontop", "--no-audio", "--no-osc", "--no-input-default-bindings", "--keep-open=yes", "--title=GfnFullscreenSplash", videoPath })
                options.ArgumentList.Add(arg);
            player = Process.Start(options);
            if (player == null) return false;

            var readyBy = Stopwatch.StartNew();
            while (readyBy.Elapsed < TimeSpan.FromSeconds(5))
            {
                player.Refresh();
                if (player.HasExited) return false;
                if (player.MainWindowHandle != IntPtr.Zero) break;
                Thread.Sleep(50);
            }
            player.Refresh();
            if (player.MainWindowHandle == IntPtr.Zero) return false;

            var playback = Stopwatch.StartNew();
            if (launchGfn)
            {
                Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true });
                startedGfn = true;
            }

            IntPtr hwnd = IntPtr.Zero;
            while (playback.Elapsed < TimeSpan.FromSeconds(45))
            {
                hwnd = FindGfnMainHwnd();
                if (hwnd != IntPtr.Zero && playback.Elapsed >= TimeSpan.FromSeconds(3.1)) break;
                if (player.HasExited) break;
                Thread.Sleep(100);
            }
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                ForceFullscreen(hwnd);
            }
            player.CloseMainWindow();
            if (!player.WaitForExit(1500)) player.Kill();
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            return true;
        }
        catch when (!startedGfn)
        {
            return false;
        }
        finally
        {
            if (player != null)
            {
                try { if (!player.HasExited) player.Kill(); } catch { }
                player.Dispose();
            }
        }
    }

    static void ShowSplashThenLaunchGfn(string splashPath, string gfnPath)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Topmost = true,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };

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

        var image = new Image { Source = frames[0], Stretch = Stretch.Uniform };
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
                    ForceFullscreen(gfnHwnd);
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
                return;
            }

            Task.Run(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(250);
                    var hwnd = FindGfnMainHwnd();
                    if (hwnd != IntPtr.Zero)
                    {
                        gfnHwnd = hwnd;
                        app.Dispatcher.Invoke(FadeOutAndHandoff);
                        return;
                    }
                }
                app.Dispatcher.Invoke(() => window.Close());
            });
        }

        window.ContentRendered += (_, _) =>
        {
            LockSetForegroundWindow(LSFW_LOCK);

            int frameIndex = 0;
            var animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delays[0]) };
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

            if (!gfnLaunched)
            {
                gfnLaunched = true;
                Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true });
            }

            var detectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            detectTimer.Tick += (_, _) =>
            {
                if (gfnHwnd != IntPtr.Zero) return;
                var hwnd = FindGfnMainHwnd();
                if (hwnd != IntPtr.Zero)
                {
                    gfnHwnd = hwnd;
                    ForceFullscreen(hwnd);
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

    static void MonitorGfn(EventWaitHandle launchEvent, string gfnPath)
    {
        DateTime lastSeen = DateTime.UtcNow;

        WinEventDelegate callback = (_, __, hwnd, idObject, ___, ____, _____) =>
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW) return;
            if (hwnd != FindGfnMainHwnd()) return;
            ForceFullscreen(hwnd);
        };

        var pinned = GCHandle.Alloc(callback);

        var hook1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var hook2 = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var hook3 = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        SetTimer(IntPtr.Zero, UIntPtr.Zero, 500, IntPtr.Zero);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_TIMER)
            {
                if (IsGfnRunning()) lastSeen = DateTime.UtcNow;
                if (launchEvent.WaitOne(0))
                {
                    lastSeen = DateTime.UtcNow;
                    var hwnd = FindGfnMainHwnd();
                    bool launchGfn = hwnd == IntPtr.Zero;
                    if (launchGfn && !IsGfnRunning()) EnsureNativeTitleBar();
                    if (!ShowMpvSplash(gfnPath, launchGfn))
                    {
                        if (hwnd != IntPtr.Zero)
                        {
                            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                            ForceFullscreen(hwnd);
                            SetForegroundWindow(hwnd);
                        }
                        else
                        {
                            try { Process.Start(new ProcessStartInfo { FileName = gfnPath, UseShellExecute = true }); }
                            catch (Exception ex) { MessageBox.Show(ex.Message, "Could not start GeForce NOW", MessageBoxButton.OK, MessageBoxImage.Error); }
                        }
                    }
                    lastSeen = DateTime.UtcNow;
                }
                var mainWindow = FindGfnMainHwnd();
                if (mainWindow != IntPtr.Zero) ForceFullscreen(mainWindow);
                if (DateTime.UtcNow - lastSeen > MonitorGraceAfterExit) break;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWinEvent(hook1);
        UnhookWinEvent(hook2);
        UnhookWinEvent(hook3);
        pinned.Free();
    }

    static void WaitAndForceFullscreen()
    {
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            var hwnd = FindGfnMainHwnd();
            if (hwnd != IntPtr.Zero)
            {
                ForceFullscreen(hwnd);
                return;
            }
        }
    }

    static void ForceFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return;

        // Restoring first is required when GFN is in WS_MAXIMIZE state; otherwise
        // SetWindowPos fights the maximized chrome (-6,-6 offsets).
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_MAXIMIZE) != 0 || (style & WS_CAPTION) != 0)
            ShowWindow(hwnd, SW_RESTORE);

        style = GetWindowLongPtr(hwnd, GWL_STYLE);
        long desired = style;
        desired &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU |
                     WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE);
        desired |= WS_POPUP;

        if (desired != style)
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)desired);

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long desiredEx = ex & ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        if (desiredEx != ex)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)desiredEx);

        GetWindowRect(hwnd, out var rect);
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        bool covers = rect.Left <= 0 && rect.Top <= 0 &&
                      rect.Right >= screenW && rect.Bottom >= screenH;

        if (!covers || desired != style || desiredEx != ex)
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, screenW, screenH,
                SWP_FRAMECHANGED | SWP_NOZORDER | SWP_SHOWWINDOW);
        }
    }

    // NVIDIA updates can restore custom chrome. Patch only while GFN is stopped.
    static void EnsureNativeTitleBar()
    {
        var jsonPath = ResolveGfnJsonPath();
        if (jsonPath == null) return;
        try
        {
            var content = File.ReadAllText(jsonPath);
            var replaced = content.Replace("nv-custom-black-window=true", "nv-custom-black-window=false");
            if (replaced != content) File.WriteAllText(jsonPath, replaced);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool IsGfnRunning()
    {
        var procs = Process.GetProcessesByName(GfnProcessName);
        bool any = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return any;
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static bool IsGfnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.Equals(GfnProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static IntPtr FindGfnMainHwnd()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        foreach (var hwnd in EnumGfnWindows())
        {
            if (!GetWindowRect(hwnd, out var r)) continue;
            long area = IsIconic(hwnd) ? 1 : (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
        }

        return best;
    }

    static List<IntPtr> EnumGfnWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!IsGfnWindow(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            // Skip tool/owned popups with no title and tiny size
            GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (!IsIconic(hwnd) && (w < 200 || h < 200)) return true;
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static string? ResolveGfnJsonPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, GfnJsonRelativePath);
        return File.Exists(path) ? path : null;
    }

    static string? ResolveGfnPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, GfnRelativePath);
        return File.Exists(path) ? path : null;
    }

    static string? FindSplashPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "nvidia.gif");
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
                key.SetValue(RunKeyName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunKeyName, false);
        }
    }

    // ── Win32 Interop ───────────────────────────────────────────

    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_THICKFRAME = 0x00040000L;
    const long WS_SYSMENU = 0x00080000L;
    const long WS_MINIMIZEBOX = 0x00020000L;
    const long WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_MAXIMIZE = 0x01000000L;
    const long WS_POPUP = 0x80000000L;
    const long WS_EX_WINDOWEDGE = 0x00000100L;
    const long WS_EX_CLIENTEDGE = 0x00000200L;
    const long WS_EX_DLGMODALFRAME = 0x00000001L;
    const long WS_EX_STATICEDGE = 0x00020000L;

    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;
    const int SW_RESTORE = 9;
    const uint GW_OWNER = 4;

    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

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
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool LockSetForegroundWindow(uint uLockCode);
    const uint LSFW_LOCK = 1;
    const uint LSFW_UNLOCK = 2;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

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
