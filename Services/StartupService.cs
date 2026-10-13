using Microsoft.Win32;

namespace FSRestore.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "FSRestore";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                // Environment.ProcessPath works in both regular and
                // single-file published apps (Assembly.Location returns ""
                // for single-file).
                var exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    key.SetValue(AppName, $"\"{exe}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
        }
    }
}
