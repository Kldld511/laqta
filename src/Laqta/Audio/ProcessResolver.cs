using System.Runtime.InteropServices;

namespace Laqta.Audio;

public sealed record ProcTarget(int Pid, string Name);

/// <summary>Finds which processes feed each audio track (game / Discord / browser).</summary>
internal static class ProcessResolver
{
    private static readonly HashSet<string> DiscordNames = new(StringComparer.OrdinalIgnoreCase)
        { "discord", "discordptb", "discordcanary", "discorddevelopment", "vesktop", "legcord" };

    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen", "librewolf", "waterfox", "thorium", "chromium" };

    private static readonly HashSet<string> NeverGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
        "applicationframehost", "taskmgr", "lockapp", "dwm", "systemsettings", "laqta", "steamwebhelper",
        "nvidia overlay", "nvcontainer", "searchapp", "widgets", "snippingtool", "screenclippinghost", "idle", "system",
    };

    private static int _gamePid;
    private static bool _gameLocked;

    public readonly record struct ProcInfo(int Parent, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);

    public static Dictionary<int, ProcInfo> Snapshot()
    {
        var map = new Dictionary<int, ProcInfo>();
        var snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return map;
            do
            {
                var name = e.szExeFile ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                map[(int)e.th32ProcessID] = new ProcInfo((int)e.th32ParentProcessID, name);
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Top-most processes of each matching app (children are covered by the loopback's process tree).</summary>
    private static List<ProcTarget> Roots(Dictionary<int, ProcInfo> procs, Func<string, bool> match)
    {
        var list = new List<ProcTarget>();
        foreach (var (pid, info) in procs)
        {
            if (pid == 0 || !match(info.Name)) continue;
            if (procs.TryGetValue(info.Parent, out var parent) && parent.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new ProcTarget(pid, info.Name));
            if (list.Count >= 4) break;
        }
        return list;
    }

    public static List<ProcTarget> Resolve(TrackKind kind, string target, Dictionary<int, ProcInfo> procs) => kind switch
    {
        TrackKind.Discord => Roots(procs, DiscordNames.Contains),
        TrackKind.Browser => Roots(procs, BrowserNames.Contains),
        TrackKind.Game => string.IsNullOrWhiteSpace(target)
            ? AutoGame(procs)
            : Roots(procs, n => n.Equals(target, StringComparison.OrdinalIgnoreCase)),
        _ => new List<ProcTarget>(),
    };

    /// <summary>
    /// Auto mode: follow the focused app, ignoring Discord, browsers and shell windows.
    /// Once a fullscreen app has been picked it stays picked until another fullscreen app takes focus.
    /// </summary>
    private static List<ProcTarget> AutoGame(Dictionary<int, ProcInfo> procs)
    {
        if (_gamePid != 0 && !procs.ContainsKey(_gamePid)) { _gamePid = 0; _gameLocked = false; }

        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint upid);
            int pid = (int)upid;
            if (pid != 0 && pid != Environment.ProcessId && procs.TryGetValue(pid, out var info)
                && !NeverGame.Contains(info.Name) && !DiscordNames.Contains(info.Name) && !BrowserNames.Contains(info.Name))
            {
                bool fullscreen = IsFullscreen(hwnd);
                if (fullscreen) { _gamePid = pid; _gameLocked = true; }
                else if (!_gameLocked) _gamePid = pid;
            }
        }

        var list = new List<ProcTarget>();
        if (_gamePid != 0 && procs.TryGetValue(_gamePid, out var g)) list.Add(new ProcTarget(_gamePid, g.Name));
        return list;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;
        return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
    }

    /// <summary>Apps with a visible window, for the manual "game source" picker.</summary>
    public static List<string> WindowedApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle)
                    && !NeverGame.Contains(p.ProcessName))
                    names.Add(p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return names.ToList();
    }
}
