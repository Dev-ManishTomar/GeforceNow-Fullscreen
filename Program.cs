using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GfnFullscreen;

static class Program
{
    const string GfnProcessName = "GeForceNOW";
    const string GfnRelativePath = @"NVIDIA Corporation\GeForceNOW\CEF\GeForceNOW.exe";
    const string GfnJsonRelativePath = @"NVIDIA Corporation\GeForceNOW\CEF\GeForceNOW.json";
    const string RunKeyName = "GfnFullscreen";

    static bool _titleBarStripped;

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

        // If GFN is already running, just strip its titlebar and exit
        var existingProc = FindGfnProcess();
        if (existingProc != null && existingProc.MainWindowHandle != IntPtr.Zero)
        {
            StripTitleBar(existingProc.MainWindowHandle);
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
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(800))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeIn.Completed += (_, _) =>
            {
                StripTitleBar(gfnHwnd);
                ShowWindow(gfnHwnd, SW_SHOW);
                LockSetForegroundWindow(LSFW_UNLOCK);
                SetForegroundWindow(gfnHwnd);
                window.Close();
            };
            fadeOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
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

            // Monitor GFN: hide its window immediately when it appears
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            hideTimer.Tick += (_, _) =>
            {
                if (gfnHwnd != IntPtr.Zero) return;

                var proc = FindGfnProcess();
                if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                {
                    gfnHwnd = proc.MainWindowHandle;
                    ShowWindow(gfnHwnd, SW_HIDE);
                    hideTimer.Stop();
                }
            };
            hideTimer.Start();
        };

        window.Closed += (_, _) =>
        {
            LockSetForegroundWindow(LSFW_UNLOCK);
            app.Shutdown();
        };

        app.Run(window);
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
        if (hwnd == IntPtr.Zero || _titleBarStripped)
            return;

        _titleBarStripped = true;

        SendMessage(hwnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU |
                   WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, w, h, SWP_FRAMECHANGED);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, w, h, SWP_FRAMECHANGED);

        SendMessage(hwnd, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
            RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME | RDW_ERASE);
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

    static string? ResolveGfnJsonPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, GfnJsonRelativePath);
        return File.Exists(path) ? path : null;
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
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const int WM_SETREDRAW = 0x000B;
    const uint RDW_INVALIDATE = 0x0001;
    const uint RDW_ALLCHILDREN = 0x0080;
    const uint RDW_FRAME = 0x0400;
    const uint RDW_ERASE = 0x0004;

    const int SW_HIDE = 0;
    const int SW_SHOW = 5;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly IntPtr HWND_NOTOPMOST = new(-2);

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
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate,
        IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool LockSetForegroundWindow(uint uLockCode);
    const uint LSFW_LOCK = 1;
    const uint LSFW_UNLOCK = 2;
}
