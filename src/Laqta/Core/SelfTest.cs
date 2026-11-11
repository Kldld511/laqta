using System.Text;
using Laqta.Audio;

namespace Laqta.Core;

/// <summary>
/// Headless end-to-end check: `Laqta.exe --selftest &lt;outputDir&gt; [gameProcessName]`.
/// Captures for a few seconds, saves an instant replay, a recording and a screenshot,
/// and writes what happened to selftest.log in the output folder.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string outputDir, string gameProcess)
    {
        Directory.CreateDirectory(outputDir);
        var log = new StringBuilder();
        void Say(string s) { log.AppendLine(s); File.WriteAllText(Path.Combine(outputDir, "selftest.log"), log.ToString()); }
        int code = 0;
        var settings = new AppSettings { OutputDir = outputDir, ReplaySeconds = 6, ReplayEnabled = true };
        settings.Game.Target = gameProcess;
        using var audio = new AudioEngine(settings);
        using var engine = new CaptureEngine(settings, audio);
        engine.Faulted += e => Say("FAULT: " + e);
        try
        {
            Say("ffmpeg: " + FfmpegLocator.Find());
            foreach (var d in Displays.List()) Say($"display {d.Index}: {d.DeviceName} {d.Width}x{d.Height} @ {d.X},{d.Y}");
            foreach (var m in CoreAudio.ListMicrophones()) Say("mic: " + m.Name);

            await engine.StartAsync();
            Say($"engine started, encoder = {engine.Encoder!.Label} (dda={engine.Encoder.DesktopDuplication}, download={engine.Encoder.Download})");

            var peaks = new float[4];
            for (int i = 0; i < 90; i++)
            {
                await Task.Delay(100);
                for (int t = 0; t < 4; t++) { peaks[t] = Math.Max(peaks[t], audio.Tracks[t].Peak); audio.Tracks[t].Peak = 0; }
            }
            for (int t = 0; t < 4; t++)
                Say($"track {audio.Tracks[t].Kind}: source='{audio.Tracks[t].SourceLabel}' peak={peaks[t]:0.000}");

            var replay = await engine.SaveReplayAsync();
            Say("replay: " + replay + " size=" + new FileInfo(replay!).Length);

            await engine.StartRecordingAsync();
            await Task.Delay(5000);
            var rec = await engine.StopRecordingAsync();
            Say("recording: " + rec + " size=" + new FileInfo(rec!).Length);

            var shot = Screenshot.Capture(engine.Display!, outputDir);
            Say("screenshot: " + shot + " size=" + new FileInfo(shot).Length);

            await engine.StopAsync();
            Say("OK");
        }
        catch (Exception ex)
        {
            Say("ERROR: " + ex);
            code = 1;
        }
        return code;
    }
}
