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

    // How long after the game loses foreground we still treat a shrunken
    // window as "the game did this" rather than a deliberate user resize.
    // Keep it short — anything longer fights manual window resizing.
    private static readonly TimeSpan RecentGameGrace = TimeSpan.FromSeconds(3);

    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventSystemSwitchEnd = 0x0015;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectShow = 0x8002;

    // Fired by the shell only for user-initiated move/size drags — the most
    // reliable signal that the user, not a game, changed a window's geometry.
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;

    private readonly WinEventDelegate _eventProc;
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _poll;

    // Snapshot of which target windows covered their work area, captured
    // while the user is NOT gaming. Frozen for the whole game session and the
    // short grace period after it, so the repair restores exactly the windows
    // that were fullscreen before the game — never one the user keeps windowed.
    private readonly Dictionary<IntPtr, bool> _targetWasFullscreen = new();

    // Windows the user resized by hand — never auto-restored until they are
    // fullscreen again. Also the rect snapshot used to detect that drag.
    private readonly HashSet<IntPtr> _userResized = [];
    private readonly Dictionary<IntPtr, RECT> _lastRects = new();

    private HashSet<string> _gameProcesses = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _targetProcesses = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private bool _gameWasForeground;
    private DateTime _lastGameForegroundUtc = DateTime.MinValue;
    
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
            [EventSystemMoveSizeStart, EventSystemMoveSizeEnd],
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
            case EventSystemMoveSizeStart:
                // User grabbed a window. Latch it so nothing snaps it back
                // mid-drag, whatever the poll timer happens to catch.
                if (hWnd != IntPtr.Zero && IsProcess(hWnd, _targetProcesses))
                {
                    _userResized.Add(hWnd);
                }
                break;
            case EventSystemMoveSizeEnd:
                // Drag finished. If the user put it back to fullscreen, the
                // latch is cleared by the next status refresh; otherwise it
                // stays latched and we leave the window alone.
                break;
            case EventObjectLocationChange:
            case EventObjectShow:
            case EventSystemMinimizeEnd:
                if (hWnd != IntPtr.Zero && idObject == ObjidWindow && IsProcess(hWnd, _targetProcesses))
                {
                    // Refresh the layout snapshot only outside a game session.
                    // While a game is foreground, or during the grace period
                    // right after it, sizes are game-affected — don't record.
                    var gameNow = AnyGameForeground();
                    var recentGame = DateTime.UtcNow - _lastGameForegroundUtc < RecentGameGrace;
                    if (!gameNow && !recentGame)
                    {
                        UpdateTargetState(hWnd);
                    }

                    if (recentGame)
                    {
                        ArmRepair();
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
            var recentGame = DateTime.UtcNow - _lastGameForegroundUtc < RecentGameGrace;
            if (recentGame)
            {
                ArmRepair();
            }

            // Focus is on a real (non-game) window now — safe to re-read the
            // layout once the grace period has passed.
            if (!recentGame)
            {
                UpdateTargetState(hWnd);
            }
        }

        _gameWasForeground = isGame;
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

        // Only refresh the snapshot while the user is not gaming and the
        // grace period has passed — that is when window sizes reflect the
        // user's real intent. During a game session the snapshot is frozen.
        var recentGame = DateTime.UtcNow - _lastGameForegroundUtc < RecentGameGrace;
        if (!gameNow && !recentGame)
        {
            RefreshTargetStates();
        }

        TrackUserResize(TargetWindows());

        RepairTargets();
    }

    private void ArmRepair()
    {
        RepairTargets();
    }

    // Records the user's own window layout, outside a game session.
    // Promotion to "fullscreen" happens only when a window genuinely covers
    // its work area — a small window is never assumed to be broken.
    // Demotion happens only when Windows has cleared WS_MAXIMIZE, i.e. the
    // user really put the window into windowed mode; a window the game just
    // shrank keeps its state so slow-to-recover apps are not dropped.
    private void UpdateTargetState(IntPtr hWnd)
    {
        if (!IsProcess(hWnd, _targetProcesses) || IsIconic(hWnd))
        {
            return;
        }

        if (IsZoomed(hWnd) && CoversWorkArea(hWnd))
        {
            _targetWasFullscreen[hWnd] = true;
            _userResized.Remove(hWnd);
            return;
        }

        // Not covering the work area and not zoomed means the user moved it
        // into real windowed mode — drop it from the repair set.
        if (!IsZoomed(hWnd))
        {
            _targetWasFullscreen[hWnd] = false;
        }
    }

    private void RefreshTargetStates()
    {
        var current = TargetWindows();

        var dead = _targetWasFullscreen.Keys.Where(h => !current.Contains(h)).ToList();
        foreach (var h in dead)
        {
            _targetWasFullscreen.Remove(h);
            _userResized.Remove(h);
            _lastRects.Remove(h);
        }

        foreach (var h in current)
        {
            if (IsIconic(h))
            {
                continue;
            }

            if (IsZoomed(h) && CoversWorkArea(h))
            {
                _targetWasFullscreen[h] = true;
                _userResized.Remove(h);
                continue;
            }

            // Not covering the work area. Demote only when Windows has cleared
            // WS_MAXIMIZE — that means the user deliberately moved the window
            // into windowed mode. A window that is still zoomed but small was
            // shrunk by the game, so it keeps its place in the repair set and
            // slow-to-recover apps (Spotify) are not dropped.
            //
            // Windows are never PROMOTED here: nothing is treated as broken
            // just for being small, so a window the user keeps in windowed
            // mode is left alone even if it carries the maximize style.
            if (!IsZoomed(h))
            {
                _targetWasFullscreen[h] = false;
            }
        }
    }

    /// <summary>
    /// Detects a genuine hand resize: the left mouse button is held AND the
    /// window under the cursor is the one whose rect changed. Checking the
    /// cursor is essential — alt-tabbing by clicking also holds the button
    /// while windows resize, and that must not be mistaken for a user resize.
    /// </summary>
    private void TrackUserResize(IReadOnlyList<IntPtr> targets)
    {
        if (!IsLeftMouseButtonDown())
        {
            foreach (var h in targets)
            {
                if (GetWindowRect(h, out var r))
                {
                    _lastRects[h] = r;
                }
            }
            return;
        }

        // The window the user is actually interacting with right now.
        GetCursorPos(out var pt);
        var underCursor = GetAncestor(WindowFromPoint(pt.X, pt.Y), GaRoot);

        foreach (var hWnd in targets)
        {
            if (!GetWindowRect(hWnd, out var r))
            {
                continue;
            }

            if (_lastRects.TryGetValue(hWnd, out var prev)
                && hWnd == underCursor
                && (prev.Left != r.Left || prev.Top != r.Top || prev.Right != r.Right || prev.Bottom != r.Bottom))
            {
                _userResized.Add(hWnd);
            }

            _lastRects[hWnd] = r;
        }
    }

    private bool SameProcess(IntPtr a, IntPtr b)
    {
        GetWindowThreadProcessId(a, out var pa);
        GetWindowThreadProcessId(b, out var pb);
        return pa == pb && pa != 0;
    }

    private void RepairTargets()
    {
        // While the left button is held the user is dragging or resizing a
        // window. Repairing now would fight the drag — the window would snap
        // back on every mouse move. Wait until the button is released.
        if (IsLeftMouseButtonDown())
        {
            return;
        }

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

            // A genuinely maximized window that covers its work area is
            // fullscreen right now. Both conditions matter: size alone is not
            // enough, because a window the user stretched to screen size by
            // hand is still a windowed window. Release the "user resized"
            // latch here too — this runs during a game session as well, so
            // maximizing a window and jumping straight into a game registers.
            if (IsZoomed(hWnd) && CoversWorkArea(hWnd))
            {
                _targetWasFullscreen[hWnd] = true;
                _userResized.Remove(hWnd);
                continue;
            }

            // The snapshot is the only source of truth for what to restore:
            // a window the user keeps in windowed mode was never marked
            // fullscreen, so it is left untouched.
            var wasFullscreen = _targetWasFullscreen.TryGetValue(hWnd, out var was) && was;
            if (!wasFullscreen)
            {
                continue;
            }

            // The broken state this app fixes always carries WS_MAXIMIZE:
            // the game shrank the rect but left the maximize style in place.
            // No maximize style means the window is genuinely windowed — the
            // user un-maximized it (double-click on the caption, Win+Arrow,
            // window menu) — so drop it right away and leave it alone.
            //
            // This check must NOT wait for the post-game grace period:
            // un-maximizing briefly after a game is exactly when the user
            // acts, and the grace period suppresses the snapshot refresh.
            if (!IsZoomed(hWnd))
            {
                _targetWasFullscreen[hWnd] = false;
                _userResized.Remove(hWnd);
                continue;
            }

            // Never touch a window the user resized by hand — detected precisely
            // by watching rect changes while the mouse button is held. It is
            // released automatically once the window is fullscreen again.
            if (_userResized.Contains(hWnd))
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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int VkLButton = 0x01;

    // True while the user is holding the left mouse button — i.e. dragging
    // or resizing a window. We never reposition anything during that.
    private static bool IsLeftMouseButtonDown() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
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
