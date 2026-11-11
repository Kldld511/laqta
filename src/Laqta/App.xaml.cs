using System.Windows;
using Laqta.Core;

namespace Laqta;

public partial class App : Application
{
    private Mutex? _single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            int code = await SelfTest.RunAsync(e.Args[1], e.Args.Length > 2 ? e.Args[2] : "");
            Shutdown(code);
            return;
        }

        _single = new Mutex(true, "Laqta.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Laqta is already running (check the system tray).", "Laqta");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Write("Unhandled: " + ev.Exception);
            ev.Handled = true;
        };

        CaptureEngine.CleanStaleBuffers();
        var settings = AppSettings.Load();
        Loc.Apply(settings.Language);
        var window = new MainWindow(settings);
        MainWindow = window;
        if (!e.Args.Contains("--minimized")) window.Show();

        // Developer aid: `Laqta.exe --snapshot <dir>` renders each page to a PNG and exits.
        int snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length) await window.SnapshotAsync(e.Args[snap + 1]);
    }
}

/// <summary>Runtime-switchable UI strings (Arabic / English resource dictionaries).</summary>
public static class Loc
{
    public static string Language { get; private set; } = "ar";
    public static bool IsRtl => Language == "ar";

    public static void Apply(string language)
    {
        Language = language == "en" ? "en" : "ar";
        var dicts = Application.Current.Resources.MergedDictionaries;
        var strings = new ResourceDictionary { Source = new Uri($"Strings/Strings.{Language}.xaml", UriKind.Relative) };
        for (int i = dicts.Count - 1; i >= 0; i--)
            if (dicts[i].Source != null && dicts[i].Source.OriginalString.Contains("Strings/")) dicts.RemoveAt(i);
        dicts.Add(strings);
    }

    public static string T(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
