using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Fensterwechler.Native;

namespace Fensterwechler;

/// <summary>Snapshot of a top-level window. The handle is only valid for the current session.</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string? ProcessPath { get; init; }

    ImageSource? icon;
    bool iconLoaded;
    /// <summary>Loaded on first use, so only rows actually displayed pay for it.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!iconLoaded) { icon = WindowDiscovery.GetIcon(Handle, ProcessPath); iconLoaded = true; }
            return icon;
        }
    }
}

public static class WindowDiscovery
{
    static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Visible, activatable top-level windows of other processes, in Z-order.</summary>
    public static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        var processCache = new Dictionary<uint, (string Name, string? Path)>();
        uint ownPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsCandidate(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid) return true;
                var className = GetClassName(hwnd);
                if (ShellClasses.Contains(className)) return true;
                if (!processCache.TryGetValue(pid, out var process)) processCache[pid] = process = GetProcess(pid);
                result.Add(new WindowInfo
                {
                    Handle = hwnd,
                    Title = GetTitle(hwnd),
                    ClassName = className,
                    ProcessId = pid,
                    ProcessName = process.Name,
                    ProcessPath = process.Path,
                });
            }
            catch (Exception ex) { Log.Debug($"Fenster 0x{hwnd:X} übersprungen: {ex.Message}"); }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        // Cloaked = suspended UWP app or window on another virtual desktop; cannot be shown anyway.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowTextLengthW(hwnd) > 0;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(GetWindowTextLengthW(hwnd) + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static (string Name, string? Path) GetProcess(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, sb, ref size))
                    return (Path.GetFileName(sb.ToString()), sb.ToString());
            }
            finally { CloseHandle(handle); }
        }
        try { return (Process.GetProcessById((int)pid).ProcessName + ".exe", null); }
        catch (ArgumentException) { return ("?", null); }
    }

    internal static ImageSource? GetIcon(IntPtr hwnd, string? processPath)
    {
        // Icons from WM_GETICON / class belong to the target process: copy, never destroy.
        IntPtr hIcon = IntPtr.Zero;
        if (SendMessageTimeoutW(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out var r) != IntPtr.Zero) hIcon = r;
        if (hIcon == IntPtr.Zero && SendMessageTimeoutW(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out r) != IntPtr.Zero) hIcon = r;
        if (hIcon == IntPtr.Zero) hIcon = GetClassLong(hwnd, GCLP_HICONSM);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLong(hwnd, GCLP_HICON);
        try
        {
            if (hIcon != IntPtr.Zero) return Freeze(Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
            if (processPath != null && File.Exists(processPath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon != null) return Freeze(Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException) { }
        return null;
    }

    static ImageSource Freeze(BitmapSource source) { source.Freeze(); return source; }
}

public static class WindowMatcher
{
    /// <summary>
    /// Finds the window for a saved entry. Exact title wins; otherwise only an unambiguous candidate
    /// (same process, same window class, optional title filter) is accepted. Showing nothing is
    /// better than showing the wrong window on a production monitor.
    /// </summary>
    public static (WindowInfo? Window, bool Ambiguous) Match(RotationEntryData entry, IEnumerable<WindowInfo> windows, ISet<IntPtr> reserved, bool exactOnly)
    {
        var candidates = windows.Where(w => string.Equals(w.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(entry.TitleFilter))
            candidates = candidates.Where(w => w.Title.Contains(entry.TitleFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var samePath = candidates.Where(w => entry.ProcessPath != null && string.Equals(w.ProcessPath, entry.ProcessPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (samePath.Count > 0) candidates = samePath;

        // Windows already bound to another entry are never shared, so two entries cannot collapse onto one window.
        candidates = candidates.Where(w => !reserved.Contains(w.Handle) && (entry.ClassName.Length == 0 || w.ClassName == entry.ClassName)).ToList();

        // Identical title and class are indistinguishable anyway; reservation keeps them apart.
        var exact = candidates.FirstOrDefault(w => w.Title == entry.Title);
        if (exact != null || exactOnly) return (exact, false);

        return candidates.Count switch
        {
            1 => (candidates[0], false),
            0 => (null, false),
            _ => (null, true),
        };
    }
}

public enum ActivationOutcome { Activated, Missing, Failed }

public sealed record ActivationResult(ActivationOutcome Outcome, string? Detail = null);

public static class WindowActivator
{
    static string? lastMonitorWarning;

    public static bool IsForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        // A dialog owned by the target (e.g. a message box of the dashboard) counts as the target being shown.
        return fg != IntPtr.Zero && (fg == hwnd || GetAncestor(fg, GA_ROOTOWNER) == hwnd);
    }

    /// <summary>
    /// Restores, optionally moves and activates the window. Windows may refuse the foreground switch;
    /// the caller verifies again shortly afterwards (activation is partly asynchronous).
    /// </summary>
    public static ActivationResult Activate(IntPtr hwnd, string? targetMonitor, bool altFallback)
    {
        if (IsHungAppWindow(hwnd)) return new(ActivationOutcome.Failed, "Anwendung reagiert nicht");

        bool iconic = IsIconic(hwnd);
        if (iconic) ShowWindowAsync(hwnd, SW_RESTORE); // async: a hung target must not block the rotation
        string? note = string.IsNullOrEmpty(targetMonitor) ? null : MoveToMonitor(hwnd, targetMonitor, iconic);
        if (IsForeground(hwnd)) return new(ActivationOutcome.Activated, note);

        bool ok = SetForegroundWindow(hwnd);
        // ponytail: double ALT tap unlocks the foreground lock on most systems (AutoHotkey's approach; two taps so
        // no menu bar stays activated). Not guaranteed by Windows; disable per profile if a target reacts to ALT.
        if (!ok && altFallback && !AnyModifierDown() && TapAltTwice()) ok = SetForegroundWindow(hwnd);
        return ok || IsForeground(hwnd)
            ? new(ActivationOutcome.Activated, note)
            : new(ActivationOutcome.Failed, "Windows hat den Fensterwechsel verweigert (Fokussperre)");
    }

    /// <summary>A held Shift/Ctrl/Win would turn the injected ALT into a shortcut (e.g. ALT+SHIFT = keyboard layout switch).</summary>
    static bool AnyModifierDown() => new[] { VK_MENU, VK_SHIFT, VK_CONTROL, VK_LWIN, VK_RWIN }.Any(vk => GetAsyncKeyState(vk) < 0);

    static bool TapAltTwice()
    {
        var inputs = new INPUT[4];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = i % 2 == 1 ? KEYEVENTF_KEYUP : 0 } } };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Moves the window to the target monitor, keeping its relative position, size and maximized state.</summary>
    static string? MoveToMonitor(IntPtr hwnd, string device, bool wasIconic)
    {
        var target = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
        if (target == null)
        {
            const string warning = "Zielmonitor nicht verfügbar – Fenster bleibt auf seinem Monitor";
            if (lastMonitorWarning != device) Log.Warn($"{warning} ({device})");
            lastMonitorWarning = device;
            return warning;
        }
        lastMonitorWarning = null;

        var source = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref source) || source.szDevice == device) return null;

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowRect(hwnd, out var rect) || !GetWindowPlacement(hwnd, ref placement)) return null;
        bool maximized = IsZoomed(hwnd) || (wasIconic && (placement.flags & WPF_RESTORETOMAXIMIZED) != 0);
        // Width/height are translation invariant, so the normal rect is usable despite workspace coordinates.
        var size = maximized || wasIconic ? placement.rcNormalPosition : rect;
        var bounds = target.Bounds;
        int width = Math.Min(size.Right - size.Left, bounds.Width);
        int height = Math.Min(size.Bottom - size.Top, bounds.Height);
        int x = Math.Clamp(bounds.Left + rect.Left - source.rcMonitor.Left, bounds.Left, bounds.Right - width);
        int y = Math.Clamp(bounds.Top + rect.Top - source.rcMonitor.Top, bounds.Top, bounds.Bottom - height);

        // All asynchronous and therefore processed in order by the target's own message queue.
        if (maximized) ShowWindowAsync(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        if (maximized) ShowWindowAsync(hwnd, SW_MAXIMIZE);
        Log.Info($"Fenster \"{WindowDiscovery.GetTitle(hwnd)}\" auf {device} verschoben");
        return null;
    }

    /// <summary>Handle still alive and still owned by the same process (handles get recycled).</summary>
    public static bool IsAlive(IntPtr hwnd, uint processId)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == processId;
    }
}
