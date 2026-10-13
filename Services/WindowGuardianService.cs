using FSRestore.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace FSRestore.Services;

/// <summary>
/// Keeps target windows stretched to their monitor's work area whenever a
/// tracked game loses foreground or minimizes. Works for modern "fake
/// fullscreen" windows (no WS_MAXIMIZE) as well as true maximized ones.
///
/// Strategy: snapshot each target window's state every poll tick. When a game
/// stops being foreground, all targets that were fullscreen before are
/// force-restored to their monitor's work area via SetWindowPos — no reliance
/// on SW_MAXIMIZE, which fails on borderless/dxgi-flip windows and plays an
/// animation we don't want.
///
/// Repairs are only armed when the game was foreground recently and the user
/// has switched to something visible (not Win+D desktop), so Discord doesn't
/// pop up on launch and Win+D still shows an empty desktop.
/// </summary>
public sealed class WindowGuardianService : IDisposable
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint WineventOutofcontext = 0x0000;
    private const int ObjidWindow = 0;
    private const int GwOwner = 4;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;

    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventSystemSwitchEnd = 0x0015;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectShow = 0x8002;

    private readonly WinEventDelegate _eventProc;
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _poll;

    // hwnd → was covering work area when last seen not-minimized
    // hwnd → was covering work area when last seen not-minimized,
    // plus when we last saw it shrink (to distinguish game-shrink vs manual).
    private readonly Dictionary<IntPtr, bool> _targetWasFullscreen = new();
    private readonly Dictionary<IntPtr, DateTime> _targetShrunkAt = new();

    private HashSet<string> _gameProcesses = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _targetProcesses = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private bool _gameWasForeground;
    private DateTime _lastGameForegroundUtc = DateTime.MinValue;
    private DateTime _repairUntilUtc = DateTime.MinValue;

    public string SelfProcessName { get; } = Process.GetCurrentProcess().ProcessName;

    public WindowGuardianService()
    {
        _eventProc = OnWinEvent;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _poll.Tick += (_, _) => PollTick();
    }

    public void Configure(IEnumerable<string> gameProcesses, IEnumerable<string> targetProcesses, bool enabled)
    {
        _gameProcesses = gameProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _targetProcesses = targetProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _enabled = enabled && _targetProcesses.Count > 0;

        if (_enabled)
        {
            EnsureHooks();
            _poll.Start();
            _gameWasForeground = AnyGameForeground();
        }
        else
        {
            RemoveHooks();
            _poll.Stop();
            _targetWasFullscreen.Clear();
        }
    }

    // ---------- enumeration for the UI ----------

    public IReadOnlyList<OpenAppInfo> ListOpenApps()
    {
        var seen = new Dictionary<string, (string? Path, string Friendly)>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var exStyle = GetWindowLong(hWnd, -20);
            var isTool = (exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0;
            if (isTool || GetWindow(hWnd, GwOwner) != IntPtr.Zero || GetWindowTextLength(hWnd) == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName;
                if (!name.Equals(SelfProcessName, StringComparison.OrdinalIgnoreCase) && !seen.ContainsKey(name))
                {
                    var path = TryGetProcessPath(proc, pid);
                    var friendly = GetFriendlyName(name, path);
                    seen[name] = (path, friendly);
                }
            }
            catch
            {
                // Process lookup failed (permissions etc.) — fall back to the
                // window title so the app still shows up in the picker.
                var title = GetWindowTitle(hWnd);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var key = $"pid:{pid}";
                    if (!seen.ContainsKey(key))
                    {
                        seen[key] = (null, title);
                    }
                }
            }

            return true;
        }, IntPtr.Zero);

        return seen.Select(kv => new OpenAppInfo(kv.Key, kv.Value.Path, kv.Value.Friendly))
            .OrderBy(x => x.DisplayName)
            .ToList();
    }

    private static string? TryGetProcessPath(Process proc, uint pid)
    {
        try
        {
            return proc.MainModule?.FileName;
        }
        catch
        {
            // Access denied for some processes — use QueryFullProcessImageName.
            try
            {
                var handle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var buf = new char[1024];
                    var len = (uint)buf.Length;
                    return QueryFullProcessImageName(handle, 0, buf, ref len) ? new string(buf, 0, (int)len) : null;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetFriendlyName(string processName, string? path)
    {
        try
        {
            if (path is not null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var desc = info.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc;
                }

                var product = info.ProductName;
                if (!string.IsNullOrWhiteSpace(product))
                {
                    return product;
                }
            }
        }
        catch
        {
        }

        return processName;
    }

    // ---------- hooks ----------

    private void EnsureHooks()
    {
        if (_hooks.Count > 0)
        {
            return;
        }

        uint[][] ranges =
        [
            [EventSystemForeground, EventSystemForeground],
            [EventSystemMinimizeStart, EventSystemMinimizeEnd],
            [EventSystemSwitchEnd, EventSystemSwitchEnd],
            [EventSystemDesktopSwitch, EventSystemDesktopSwitch],
            [EventObjectShow, EventObjectShow],
            [EventObjectLocationChange, EventObjectLocationChange],
        ];

        foreach (var range in ranges)
        {
            var hook = SetWinEventHook(range[0], range[1], IntPtr.Zero, _eventProc, 0, 0, WineventOutofcontext);
            if (hook != IntPtr.Zero)
            {
                _hooks.Add(hook);
            }
        }
    }

    private void RemoveHooks()
    {
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_enabled)
        {
            return;
        }

        switch (eventType)
        {
            case EventSystemForeground:
                HandleForeground(hWnd);
                break;
            case EventObjectLocationChange:
            case EventObjectShow:
            case EventSystemMinimizeEnd:
                if (hWnd != IntPtr.Zero && idObject == ObjidWindow)
                {
                    UpdateTargetState(hWnd);
                    // A target window just appeared/moved — if it was
                    // fullscreen before, keep repair armed so a late restore
                    // still snaps it back even if the 10s game window lapsed.
                    if (IsProcess(hWnd, _targetProcesses)
                        && _targetWasFullscreen.TryGetValue(hWnd, out var was)
                        && was)
                    {
                        ArmRepair(3);
                    }
                    else if (IsProcess(hWnd, _targetProcesses)
                             && DateTime.UtcNow - _lastGameForegroundUtc < TimeSpan.FromSeconds(10))
                    {
                        ArmRepair(3);
                    }
                }
                break;
            // MinimizeStart / DesktopSwitch / SwitchEnd are intentionally not
            // armed — Win+D must leave everything hidden, and the foreground
            // event covers the actual switch to a target app.
        }
    }

    private void HandleForeground(IntPtr hWnd)
    {
        var isGame = IsProcess(hWnd, _gameProcesses);

        if (isGame)
        {
            _lastGameForegroundUtc = DateTime.UtcNow;
        }
        else
        {
            // Left the game to something else — arm repair so every
            // previously-fullscreen target snaps back, even if focus went to
            // a non-target app (browser behind Telegram, etc.).
            var recentGame = DateTime.UtcNow - _lastGameForegroundUtc < TimeSpan.FromSeconds(10);
            if (recentGame)
            {
                ArmRepair(5);
            }
        }

        _gameWasForeground = isGame;
        UpdateTargetState(hWnd);
    }

    // ---------- polling ----------

    private void PollTick()
    {
        if (!_enabled)
        {
            return;
        }

        var gameNow = AnyGameForeground();
        if (gameNow)
        {
            _lastGameForegroundUtc = DateTime.UtcNow;
        }

        _gameWasForeground = gameNow;

        RefreshTargetStates();

        // Always repair: if a tracked window was fullscreen before and is
        // currently visible but not covering its work area, snap it back.
        // The wasFullscreen flag is the guard — windows the user manually
        // shrank below fullscreen aren't in the map as "was fullscreen",
        // so we never fight deliberate resizes. AllTopLevelMinimized inside
        // RepairTargets still protects Win+D.
        RepairTargets();
    }

    private void ArmRepair(int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _repairUntilUtc)
        {
            _repairUntilUtc = until;
        }

        RepairTargets();
    }

    private void UpdateTargetState(IntPtr hWnd)
    {
        if (!IsProcess(hWnd, _targetProcesses))
        {
            return;
        }

        if (IsIconic(hWnd))
        {
            return; // keep last-known state; don't overwrite with "shrunk"
        }

        var covers = CoversWorkArea(hWnd);
        if (covers)
        {
            _targetWasFullscreen[hWnd] = true;
            _targetShrunkAt.Remove(hWnd);
            return;
        }

        // Shrunken: mark when it happened so we can distinguish a fresh
        // game-shrink (repair) from a stale manual resize (leave alone).
        _targetShrunkAt.TryAdd(hWnd, DateTime.UtcNow);
    }

    private void RefreshTargetStates()
    {
        var current = TargetWindows();
        var dead = _targetWasFullscreen.Keys.Where(h => !current.Contains(h)).ToList();
        foreach (var h in dead)
        {
            _targetWasFullscreen.Remove(h);
        }

        foreach (var h in current)
        {
            if (!_targetWasFullscreen.ContainsKey(h) && CoversWorkArea(h))
            {
                _targetWasFullscreen[h] = true;
            }
        }

        // Forget stale "was fullscreen" flags: if a window has been
        // continuously shrunken for >15s without a recent game trigger, the
        // user deliberately resized it — stop forcing it back.
        var stale = _targetWasFullscreen
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .Where(h => _targetShrunkAt.TryGetValue(h, out var t)
                        && DateTime.UtcNow - t > TimeSpan.FromSeconds(15)
                        && DateTime.UtcNow - _lastGameForegroundUtc > TimeSpan.FromSeconds(15))
            .ToList();
        foreach (var h in stale)
        {
            _targetWasFullscreen[h] = false;
        }
    }

    private void RepairTargets()
    {
        var targets = TargetWindows();

        // Win+D / "show desktop" minimizes every top-level window. In that
        // case do nothing — the user explicitly wants an empty desktop.
        if (AllTopLevelMinimized())
        {
            return;
        }

        foreach (var hWnd in targets)
        {
            if (!IsWindow(hWnd) || IsIconic(hWnd))
            {
                continue;
            }

            if (CoversWorkArea(hWnd))
            {
                _targetWasFullscreen[hWnd] = true;
                continue;
            }

            // Only repair windows that were fullscreen before, or that look
            // like a game-shrunk remnant (width=workW but height<workH, or
            // vice versa — i.e., a "cropped" rectangle from custom res).
            var wasFullscreen = _targetWasFullscreen.TryGetValue(hWnd, out var was) && was;
            var recentGame = DateTime.UtcNow - _lastGameForegroundUtc < TimeSpan.FromSeconds(10);
            if (!wasFullscreen && !(recentGame && LooksCropped(hWnd)))
            {
                continue;
            }

            // Fix instantly via SetWindowPos — no SW_MAXIMIZE animation.
            // NOZORDER/NOACTIVATE keep the window in place and unfocused.
            var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                continue;
            }

            var w = info.rcWork.Right - info.rcWork.Left;
            var h = info.rcWork.Bottom - info.rcWork.Top;
            SetWindowPos(
                hWnd,
                IntPtr.Zero,
                info.rcWork.Left,
                info.rcWork.Top,
                w,
                h,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
        }
    }

    /// <summary>
    /// True when every visible top-level window is minimized — i.e., the user
    /// pressed Win+D or otherwise asked for an empty desktop. We leave windows
    /// alone in that state; restoring them would defeat the point of Win+D.
    /// </summary>
    private static bool AllTopLevelMinimized()
    {
        var anyVisible = false;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0)
            {
                return true;
            }

            var exStyle = GetWindowLong(hWnd, -20);
            var isTool = (exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0;
            if (isTool || GetWindow(hWnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            if (!IsIconic(hWnd))
            {
                anyVisible = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return !anyVisible;
    }

    /// <summary>
    /// True when the window's size matches the monitor's work area in one
    /// dimension but is smaller in the other — the classic "stretched
    /// resolution left the window cropped" signature. Only applied during
    /// the armed repair window, so deliberate user resizes outside it are
    /// safe.
    /// </summary>
    private bool LooksCropped(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var r))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var workW = info.rcWork.Right - info.rcWork.Left;
        var workH = info.rcWork.Bottom - info.rcWork.Top;

        var matchesW = w >= workW - 4;
        var matchesH = h >= workH - 4;
        var smallerW = w < workW - 8 && w > workW / 2;
        var smallerH = h < workH - 8 && h > workH / 2;

        return (matchesW && smallerH) || (matchesH && smallerW);
    }

    private bool CoversWorkArea(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var r))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var workW = info.rcWork.Right - info.rcWork.Left;
        var workH = info.rcWork.Bottom - info.rcWork.Top;
        var monW = info.rcMonitor.Right - info.rcMonitor.Left;
        var monH = info.rcMonitor.Bottom - info.rcMonitor.Top;

        return (w >= workW - 4 && h >= workH - 4) || (w >= monW - 4 && h >= monH - 4);
    }

    private List<IntPtr> TargetWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0 && IsProcess(hWnd, _targetProcesses))
            {
                result.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private bool AnyGameForeground() => IsProcess(GetForegroundWindow(), _gameProcesses);

    private bool IsProcess(IntPtr hWnd, HashSet<string> names)
    {
        if (hWnd == IntPtr.Zero || names.Count == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(hWnd, out var pid);
        try
        {
            return names.Contains(Process.GetProcessById((int)pid).ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetProcessPath(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).FirstOrDefault()?.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        RemoveHooks();
    }

    // ---------- interop ----------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, char[] lpExeName, ref uint lpdwSize);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    private static long GetWindowLong(IntPtr hWnd, int nIndex) => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
