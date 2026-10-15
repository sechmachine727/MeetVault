using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MeetVault.Core;

/// <summary>Display/window enumeration and stored-source resolution for screen capture.
/// Stored form (`screen.source`): "display:&lt;deviceName&gt;" or "window:&lt;exe&gt;:&lt;title&gt;".
/// Window handles are session-local: they are resolved at record START only; resolution
/// here works on fresh enumerations so retitled windows still match by prefix.</summary>
public static class CaptureSourceResolver
{
    /// <summary>Monitors, primary first (the fallback target).</summary>
    public static IReadOnlyList<CaptureSource> Displays()
    {
        var list = new List<(CaptureSource Source, bool Primary)>();
        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
            {
                var info = new Native.MONITORINFOEX();
                info.cbSize = Marshal.SizeOf<Native.MONITORINFOEX>();
                if (Native.GetMonitorInfo(hMonitor, ref info))
                {
                    var b = info.rcMonitor;
                    var device = info.szDevice ?? "";
                    var label = device + " (" + (b.Right - b.Left) + "x" + (b.Bottom - b.Top) + ")";
                    list.Add((new CaptureSource("display", device, label), (info.dwFlags & 1) != 0)); // 1 = MONITORINFOF_PRIMARY
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { AppLog.Warn("screen: display enumeration failed: " + ex.Message); }
        return list.OrderByDescending(x => x.Primary).Select(x => x.Source).ToArray();
    }

    /// <summary>Visible, titled top-level windows, excluding our own and shell chrome.</summary>
    public static IReadOnlyList<CaptureSource> Windows()
    {
        var list = new List<CaptureSource>();
        try
        {
            Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
            {
                var w = Describe(hwnd);
                if (w is not null) list.Add(w);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { AppLog.Warn("screen: window enumeration failed: " + ex.Message); }
        return list;
    }

    /// <summary>The HWND of a window source right now, or IntPtr.Zero (resolve at START only).</summary>
    public static IntPtr FindWindowHandle(CaptureSource source)
    {
        if (source.Kind != "window") return IntPtr.Zero;
        var title = LabelTitle(source);
        var exe = source.Id.ToLowerInvariant();
        var found = IntPtr.Zero;
        try
        {
            Native.EnumWindows((IntPtr hwnd, IntPtr _) =>
            {
                if (found != IntPtr.Zero) return false;
                var w = Describe(hwnd);
                if (w is null || !string.Equals(w.Id, exe, StringComparison.OrdinalIgnoreCase)) return true;
                var wTitle = LabelTitle(w);
                if (title.Length > 0 &&
                    !wTitle.StartsWith(title, StringComparison.Ordinal) &&
                    !title.StartsWith(wTitle, StringComparison.Ordinal)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && Native.IsWindow(hwnd);

    /// <summary>Stored form for the settings ("display:&lt;device&gt;"; "window:&lt;exe&gt;:&lt;title&gt;").</summary>
    public static string Format(CaptureSource source)
        => source.Kind == "window" ? "window:" + source.Id + ":" + LabelTitle(source) : "display:" + source.Id;

    /// <summary>Resolves the stored source against fresh enumerations. Exact display match
    /// wins; windows match by exe + title prefix (retitled windows); anything else falls
    /// back to the primary display with a reason.</summary>
    public static CaptureSource Resolve(string? stored, IReadOnlyList<CaptureSource> displays,
        IReadOnlyList<CaptureSource> windows, out string reason)
    {
        if (displays.Count == 0) throw new InvalidOperationException("no display available for capture");
        reason = "";
        var s = (stored ?? "").Trim();
        if (s.Length == 0)
        {
            reason = "no source stored";
            return displays[0];
        }
        if (s.StartsWith("display:", StringComparison.OrdinalIgnoreCase))
        {
            var id = s[8..];
            foreach (var d in displays)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            reason = "stored source missing";
            return displays[0];
        }
        if (s.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s[7..];
            var sep = rest.IndexOf(':');
            var exe = (sep >= 0 ? rest[..sep] : rest).ToLowerInvariant();
            var title = sep >= 0 ? rest[(sep + 1)..] : "";
            if (exe.Length > 0)
            {
                foreach (var w in windows)
                {
                    if (!string.Equals(w.Id, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    var wTitle = LabelTitle(w);
                    if (title.Length == 0 || wTitle.StartsWith(title, StringComparison.Ordinal) ||
                        title.StartsWith(wTitle, StringComparison.Ordinal))
                        return w;
                }
            }
            reason = "stored source missing";
            return displays[0];
        }
        reason = "stored source missing";
        return displays[0];
    }

    /// <summary>The window-title part of a source label ("exe — title"); "" for displays.</summary>
    public static string LabelTitle(CaptureSource source)
    {
        var sep = source.Label.IndexOf(" — ", StringComparison.Ordinal);
        return sep >= 0 && sep + 3 <= source.Label.Length ? source.Label[(sep + 3)..] : "";
    }

    /// <summary>Null unless the hwnd is a visible, titled, non-shell, non-self window.</summary>
    private static CaptureSource? Describe(IntPtr hwnd)
    {
        try
        {
            if (!Native.IsWindowVisible(hwnd)) return null;
            var len = Native.GetWindowTextLength(hwnd);
            if (len <= 0) return null;
            var sb = new StringBuilder(len + 1);
            if (Native.GetWindowText(hwnd, sb, sb.Capacity) <= 0) return null;
            var title = sb.ToString();
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return null;
            var cls = ClassName(hwnd);
            if (cls is "Progman" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return null;
            var exe = ExeName(pid);
            if (exe.Length == 0) return null;
            return new CaptureSource("window", exe.ToLowerInvariant(), exe + " — " + title);
        }
        catch { return null; }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return Native.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string ExeName(int pid)
    {
        try
        {
            if (pid <= 0) return "";
            using var p = Process.GetProcessById(pid); // the handle must not leak per window (review r10)
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private static class Native
    {
        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);
    }
}
