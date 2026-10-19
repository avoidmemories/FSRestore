using FSRestore.Models;
using FSRestore.Services;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FSRestore;

/// <summary>
/// Owns the long-lived services: settings, the window guardian and the tray
/// icon. The main window is created lazily — a tray-only launch (the
/// autostart path) never builds the WPF visual tree at all, which keeps
/// memory and startup cost low.
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    private const uint WmShowMe = 0x0402; // WM_USER + 2

    private TrayIconService? _tray;
    private MainWindow? _window;
    private bool _exiting;

    internal SettingsStore Store { get; } = new();
    internal WindowGuardianService Guardian { get; } = new();
    internal AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            MessageBox.Show(args.ExceptionObject?.ToString() ?? "Unknown fatal error",
                "FSRestore crashed", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(),
                "FSRestore error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);

        // Single instance: if another FSRestore is already running, signal it
        // to show its window and exit instead of launching a duplicate.
        var mutex = new Mutex(true, "FSRestore_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstanceToShow();
            Shutdown(0);
            return;
        }
        _mutex = mutex;

        Settings = Store.Load();

        // Arm the guardian before any UI exists. The tray launch path never
        // creates a window, and the guardian must still run.
        ApplyGuardian();

        _tray = new TrayIconService("FSRestore", ShowMainWindow, ExitApp);

        var startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
        {
            ShowMainWindow();
        }
    }

    /// <summary>Creates the window on first use, then just re-shows it.</summary>
    internal void ShowMainWindow()
    {
        if (_exiting)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(this);
            MainWindow = _window;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    internal void SaveSettings() => Store.Save(Settings);

    internal void ApplyGuardian() =>
        Guardian.Configure(Settings.GameProcesses, Settings.TargetProcesses, Settings.Enabled);

    /// <summary>Called when the window hides itself into the tray.</summary>
    internal void NotifyHidden()
    {
        // Hand back the managed heap while the app sits idle in the tray.
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        _tray?.ShowBalloon("FSRestore", "Running in tray — right-click to open.");
    }

    internal void ExitApp()
    {
        _exiting = true;
        SaveSettings();
        _tray?.Dispose();
        _tray = null;
        Guardian.Dispose();
        Shutdown(0);
    }

    private static void SignalExistingInstanceToShow()
    {
        // Post a custom message to the existing instance's hidden tray window.
        var hwnd = FindWindow("FSRestore_TrayWindow", null);
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmShowMe, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}