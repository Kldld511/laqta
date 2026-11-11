using System.Text.Json;

namespace Laqta.Core;

public sealed class TrackSettings
{
    public bool Enabled { get; set; } = true;
    public double Gain { get; set; } = 1.0;
    /// <summary>Game: exe name ("" = auto). Mic: device id ("" = default).</summary>
    public string Target { get; set; } = "";
}

public sealed class AppSettings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Laqta");
    public int Fps { get; set; } = 60;
    public string Quality { get; set; } = "High";
    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 120;
    public int Monitor { get; set; } = 0;
    public bool CaptureCursor { get; set; } = true;
    public bool IncludeMix { get; set; } = true;

    public TrackSettings Game { get; set; } = new();
    public TrackSettings Mic { get; set; } = new();
    public TrackSettings Discord { get; set; } = new();
    public TrackSettings Browser { get; set; } = new();

    public string HotkeyRecord { get; set; } = "Ctrl+F9";
    public string HotkeyReplay { get; set; } = "Ctrl+F10";
    public string HotkeyScreenshot { get; set; } = "Ctrl+F12";

    public string Language { get; set; } = "ar";
    public bool MinimizeToTray { get; set; } = true;
    public bool Sounds { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}

public static class AppPaths
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Laqta");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string BufferRoot => Path.Combine(DataDir, "buffer");
    public static string ThumbDir => Path.Combine(DataDir, "thumbs");
    public static string LogFile => Path.Combine(DataDir, "laqta.log");
}

public static class Log
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

public static class FfmpegLocator
{
    private static string? _cached;

    public static string? Find()
    {
        if (_cached != null && File.Exists(_cached)) return _cached;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
        };
        var pathVar = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" +
                      (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "") + ";" +
                      (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(dir, "ffmpeg.exe")); } catch { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return _cached = c; } catch { }
        }
        try
        {
            var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                foreach (var dir in Directory.GetDirectories(pkgs, "Gyan.FFmpeg*"))
                {
                    var hit = Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return _cached = hit;
                }
        }
        catch { }
        return null;
    }
}
