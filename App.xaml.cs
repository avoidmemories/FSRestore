using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FSRestore;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const uint WmShowMe = 0x0402; // WM_USER + 2

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

        // Start hidden in tray when launched via "Run at startup" (--tray flag).
        var startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            if (!startHidden)
            {
                window.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "FSRestore failed to start",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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

    // Called from TrayIconService's WndProc when WmShowMe arrives — forwarded
    // to the main window to un-hide it.
    internal static void NotifyExistingInstanceWantsShow(MainWindow window)
    {
        window.Dispatcher.Invoke(() =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
