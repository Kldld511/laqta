using System.Diagnostics;
using System.Globalization;
using System.Text;
using Laqta.Audio;

namespace Laqta.Core;

/// <summary>How the screen is grabbed and which encoder compresses it.</summary>
public sealed record EncoderChoice(string Label, bool DesktopDuplication, bool Download, string Codec)
{
    public string InputArgs(DisplayInfo d, int fps, bool cursor)
    {
        int mouse = cursor ? 1 : 0;
        if (!DesktopDuplication)
            return $"-f gdigrab -framerate {fps} -draw_mouse {mouse} -offset_x {d.X} -offset_y {d.Y} -video_size {d.Width}x{d.Height} -i desktop";
        string chain = !Download ? "" : Codec switch
        {
            "h264_nvenc" => ",hwdownload,format=bgra",
            "libx264" => ",hwdownload,format=bgra,format=yuv420p",
            _ => ",hwdownload,format=bgra,format=nv12",
        };
        return $"-f lavfi -i \"ddagrab=output_idx={d.Index}:framerate={fps}:draw_mouse={mouse}{chain}\"";
    }

    private static int MaxMbps(int q) => q >= 30 ? 12 : q >= 26 ? 25 : q >= 22 ? 50 : 100;

    public string CodecArgs(int fps, int q) => Codec switch
    {
        "h264_nvenc" => $"-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq {q} -b:v 0 -maxrate {MaxMbps(q)}M -bufsize {MaxMbps(q) * 2}M -g {fps}",
        "h264_amf" => $"-c:v h264_amf -usage transcoding -quality balanced -rc cqp -qp_i {q} -qp_p {q} -qp_b {q} -g {fps}",
        "h264_qsv" => $"-c:v h264_qsv -preset medium -global_quality {q} -g {fps}",
        _ => $"-c:v libx264 -preset veryfast -crf {q} -g {fps} -keyint_min {fps} -sc_threshold 0 -pix_fmt yuv420p",
    };

    public static readonly EncoderChoice[] Candidates =
    {
        new("NVIDIA NVENC", true, false, "h264_nvenc"),
        new("AMD AMF", true, false, "h264_amf"),
        new("NVIDIA NVENC", true, true, "h264_nvenc"),
        new("AMD AMF", true, true, "h264_amf"),
        new("Intel Quick Sync", true, true, "h264_qsv"),
        new("x264 (CPU)", true, true, "libx264"),
        new("NVIDIA NVENC", false, false, "h264_nvenc"),
        new("x264 (CPU)", false, false, "libx264"),
    };

    /// <summary>Tries each capture/encoder combination with a tiny real encode and returns the first that works.</summary>
    public static async Task<EncoderChoice?> DetectAsync(string ffmpeg, DisplayInfo display)
    {
        foreach (var c in Candidates)
        {
            var args = $"-hide_banner -loglevel error {c.InputArgs(display, 30, true)} -frames:v 6 {c.CodecArgs(30, 28)} -f null -";
            var (code, err) = await FfmpegRunner.RunAsync(ffmpeg, args, null, 12000);
            Log.Write($"Encoder probe [{c.Label}, dda={c.DesktopDuplication}, download={c.Download}] exit={code}");
            if (code == 0) return c;
        }
        return null;
    }
}

public static class FfmpegRunner
{
    public static async Task<(int Code, string Err)> RunAsync(string ffmpeg, string args, string? workDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? AppContext.BaseDirectory,
        };
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "timeout");
        }
        return (p.ExitCode, await errTask);
    }
}

/// <summary>
/// Runs one FFmpeg process that continuously writes short MPEG-TS segments (video + every audio track).
/// Instant replay and normal recording are both just "cut a time range out of those segments",
/// so saving is a fast stream copy with no re-encoding.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private const int SegmentSeconds = 4;

    private readonly AppSettings _s;
    private readonly AudioEngine _audio;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _ff;
    private AudioPipe? _pipe;
    private string? _sessionDir;
    private List<string> _trackTitles = new();
    private Timer? _pruneTimer;
    private int _exports;
    private volatile bool _stopping;
    private volatile bool _videoLost;
    private bool _startedForRecording;

    public EncoderChoice? Encoder { get; private set; }
    public DisplayInfo? Display { get; private set; }
    public bool IsRunning => _ff != null;
    public bool IsStarting { get; private set; }
    public bool IsRecording { get; private set; }
    public DateTime RecordStartUtc { get; private set; }
    public DateTime EngineStartUtc { get; private set; }

    /// <summary>Raised (on any thread) whenever running / recording state changes.</summary>
    public event Action? StateChanged;
    /// <summary>Raised when FFmpeg dies unexpectedly.</summary>
    public event Action<string>? Faulted;

    public CaptureEngine(AppSettings settings, AudioEngine audio)
    {
        _s = settings;
        _audio = audio;
    }

    public static int QualityValue(string quality) => quality switch
    {
        "Low" => 30,
        "Medium" => 26,
        "Ultra" => 18,
        _ => 22,
    };

    public static void CleanStaleBuffers()
    {
        try { if (Directory.Exists(AppPaths.BufferRoot)) Directory.Delete(AppPaths.BufferRoot, true); } catch { }
    }

    public void InvalidateEncoder() => Encoder = null;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try { await StartCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        if (IsRunning) return;
        IsStarting = true;
        StateChanged?.Invoke();
        try
        {
            var ffmpeg = FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg not found");
            Display = Displays.Get(_s.Monitor);
            Encoder ??= await EncoderChoice.DetectAsync(ffmpeg, Display)
                        ?? throw new InvalidOperationException("No working screen capture / encoder combination was found");

            _sessionDir = Path.Combine(AppPaths.BufferRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(_sessionDir);

            _audio.Start();
            var tracks = _audio.EnabledTracks();
            _pipe = tracks.Count > 0 ? _audio.OpenPipe(tracks) : null;

            var args = BuildArgs(tracks);
            Log.Write("ffmpeg " + args);
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _sessionDir,
            };
            lock (_stderr) _stderr.Clear();
            _stopping = false;
            _videoLost = false;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_stderr) { _stderr.Enqueue(e.Data); while (_stderr.Count > 25) _stderr.Dequeue(); }
                Log.Write("[ffmpeg] " + e.Data);
                // Windows can revoke screen capture (full-screen switch, resolution change, lock screen).
                // FFmpeg then keeps writing audio-only segments, so stop it and let OnExited restart the capture.
                if (!_videoLost && !_stopping && IsVideoLost(e.Data))
                {
                    _videoLost = true;
                    Log.Write("Screen capture lost; restarting the capture");
                    try { p.Kill(true); } catch { }
                }
            };
            p.Exited += (_, _) => OnExited(p);
            p.Start();
            ChildJob.Adopt(p);
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            _ff = p;

            // Ready once the first segment file exists.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                if (p.HasExited) throw new InvalidOperationException("FFmpeg exited: " + StderrTail());
                if (Directory.EnumerateFiles(_sessionDir, "seg_*.ts").Any()) break;
                await Task.Delay(100);
            }
            if (!Directory.EnumerateFiles(_sessionDir, "seg_*.ts").Any())
                throw new InvalidOperationException("FFmpeg produced no output: " + StderrTail());

            EngineStartUtc = SegmentList()[0].Start;
            _pruneTimer = new Timer(_ => Prune(), null, 5000, 5000);
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally
        {
            IsStarting = false;
            StateChanged?.Invoke();
        }
    }

    private string BuildArgs(List<AudioTrack> tracks)
    {
        var sb = new StringBuilder("-hide_banner -loglevel warning -nostats -y ");
        sb.Append(Encoder!.InputArgs(Display!, _s.Fps, _s.CaptureCursor)).Append(' ');
        _trackTitles = new List<string>();

        if (_pipe != null)
        {
            int n = tracks.Count;
            sb.Append($"-thread_queue_size 1024 -f s16le -ar {CoreAudio.SampleRate} -ac {n * 2} -i {_pipe.FullPath} ");

            bool mix = _s.IncludeMix && n > 1;
            int outputs = n + (mix ? 1 : 0);
            var fc = new StringBuilder();
            fc.Append("[1:a]asplit=").Append(outputs);
            for (int i = 0; i < outputs; i++) fc.Append($"[s{i}]");
            fc.Append(';');
            var maps = new List<string>();
            int idx = 0;
            if (mix)
            {
                var left = string.Join("+", Enumerable.Range(0, n).Select(i => "c" + i * 2));
                var right = string.Join("+", Enumerable.Range(0, n).Select(i => "c" + (i * 2 + 1)));
                fc.Append($"[s{idx}]pan=stereo|c0={left}|c1={right}[mix];");
                maps.Add("[mix]");
                _trackTitles.Add("Mix");
                idx++;
            }
            for (int i = 0; i < n; i++, idx++)
            {
                fc.Append($"[s{idx}]pan=stereo|c0=c{i * 2}|c1=c{i * 2 + 1}[t{i}];");
                maps.Add($"[t{i}]");
                _trackTitles.Add(tracks[i].EnglishName);
            }
            fc.Length--; // trailing ';'
            sb.Append($"-filter_complex \"{fc}\" -map 0:v ");
            foreach (var m in maps) sb.Append($"-map {m} ");
            sb.Append("-c:a aac -b:a 192k ");
        }
        else sb.Append("-map 0:v ");

        sb.Append(Encoder.CodecArgs(_s.Fps, QualityValue(_s.Quality))).Append(' ');
        sb.Append($"-fps_mode cfr -r {_s.Fps} ");
        sb.Append($"-f segment -segment_time {SegmentSeconds} -segment_format mpegts -reset_timestamps 0 -flush_packets 1 seg_%06d.ts");
        return sb.ToString();
    }

    private string StderrTail()
    {
        lock (_stderr) return string.Join(" | ", _stderr.TakeLast(4));
    }

    internal static bool IsVideoLost(string line) =>
        line.Contains("AcquireNextFrame failed", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("in#0", StringComparison.Ordinal) && line.Contains("Error during demuxing", StringComparison.OrdinalIgnoreCase));

    private void OnExited(Process p)
    {
        if (_stopping || _ff != p) return;
        var tail = StderrTail();
        bool lost = _videoLost;
        Log.Write((lost ? "FFmpeg stopped after losing the screen: " : "FFmpeg exited unexpectedly: ") + tail);
        Task.Run(async () =>
        {
            await _gate.WaitAsync();
            bool wasRecording = false, restarted = false;
            try
            {
                if (_ff == p)
                {
                    wasRecording = IsRecording;
                    await StopCoreAsync();
                    if (lost) restarted = await TryRestartCoreAsync();
                }
            }
            finally { _gate.Release(); }
            if (!restarted || wasRecording) Faulted?.Invoke(tail);
        });
    }

    /// <summary>Retries starting the pipeline for a few seconds; the screen is usually capturable again shortly after.</summary>
    private async Task<bool> TryRestartCoreAsync()
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(1500);
            try
            {
                await StartCoreAsync();
                Log.Write($"Screen capture restarted (attempt {attempt})");
                return true;
            }
            catch (Exception ex) { Log.Write($"Restart attempt {attempt} failed: {ex.Message}"); }
        }
        return false;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _pruneTimer?.Dispose();
        _pruneTimer = null;
        var p = _ff;
        _ff = null;
        IsRecording = false;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                    using var cts = new CancellationTokenSource(4000);
                    try { await p.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
                }
            }
            catch { }
            p.Dispose();
        }
        _pipe?.Dispose();
        _pipe = null;
        _audio.Stop();

        // Wait for any in-flight export before deleting the buffer.
        var dir = _sessionDir;
        _sessionDir = null;
        if (dir != null)
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 600 && Volatile.Read(ref _exports) > 0; i++) await Task.Delay(100);
                try { Directory.Delete(dir, true); } catch { }
            });
        StateChanged?.Invoke();
    }

    /// <summary>Applies changed settings by restarting the pipeline (skipped while recording).</summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsRecording) return;
            await StopCoreAsync();
            await StartCoreAsync();
        }
        finally { _gate.Release(); }
    }

    private sealed record Segment(string Path, DateTime Start);

    private List<Segment> SegmentList()
    {
        var dir = _sessionDir;
        if (dir == null || !Directory.Exists(dir)) return new List<Segment>();
        return Directory.GetFiles(dir, "seg_*.ts").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new Segment(f, File.GetCreationTimeUtc(f))).ToList();
    }

    private void Prune()
    {
        if (Volatile.Read(ref _exports) > 0) return;
        try
        {
            var keepFrom = DateTime.UtcNow.AddSeconds(-(_s.ReplayEnabled ? _s.ReplaySeconds : 0) - 3 * SegmentSeconds);
            if (IsRecording && RecordStartUtc.AddSeconds(-SegmentSeconds) < keepFrom) keepFrom = RecordStartUtc.AddSeconds(-SegmentSeconds);
            var segs = SegmentList();
            for (int i = 0; i < segs.Count - 1; i++)
            {
                if (segs[i + 1].Start < keepFrom) { try { File.Delete(segs[i].Path); } catch { } }
                else break;
            }
        }
        catch { }
    }

    public async Task StartRecordingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRecording) return;
            if (!IsRunning)
            {
                await StartCoreAsync();
                _startedForRecording = true;
                RecordStartUtc = EngineStartUtc;
            }
            else
            {
                _startedForRecording = false;
                RecordStartUtc = DateTime.UtcNow;
            }
            IsRecording = true;
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    public async Task<string?> StopRecordingAsync()
    {
        if (!IsRecording || !IsRunning) return null;
        var to = DateTime.UtcNow;
        var from = RecordStartUtc;
        var path = NewFile("Rec");
        Interlocked.Increment(ref _exports);
        try
        {
            IsRecording = false;
            StateChanged?.Invoke();
            await ExportAsync(from, to, path);
        }
        finally { Interlocked.Decrement(ref _exports); }
        if (!_s.ReplayEnabled || (_startedForRecording && !_s.ReplayEnabled)) await StopAsync();
        return path;
    }

    /// <summary>Saves the last N seconds (the instant-replay window) to a file.</summary>
    public async Task<string?> SaveReplayAsync()
    {
        if (!IsRunning) return null;
        var to = DateTime.UtcNow;
        var from = to.AddSeconds(-_s.ReplaySeconds);
        if (from < EngineStartUtc) from = EngineStartUtc;
        var path = NewFile("Replay");
        Interlocked.Increment(ref _exports);
        try { await ExportAsync(from, to, path); }
        finally { Interlocked.Decrement(ref _exports); }
        return path;
    }

    private string NewFile(string kind)
    {
        Directory.CreateDirectory(_s.OutputDir);
        var stem = $"Laqta_{kind}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var path = Path.Combine(_s.OutputDir, stem + ".mp4");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(_s.OutputDir, $"{stem}_{n}.mp4");
        return path;
    }

    private async Task ExportAsync(DateTime from, DateTime to, string outPath)
    {
        var dir = _sessionDir ?? throw new InvalidOperationException("Capture is not running");
        var ffmpeg = FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg not found");

        // Give the encoder and muxer a moment to flush everything up to "now" to disk.
        var wait = to.AddMilliseconds(1300) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        var segs = SegmentList();
        var chosen = new List<Segment>();
        for (int i = 0; i < segs.Count; i++)
        {
            var end = i + 1 < segs.Count ? segs[i + 1].Start : DateTime.MaxValue;
            if (end > from && segs[i].Start < to) chosen.Add(segs[i]);
        }
        if (chosen.Count == 0) throw new InvalidOperationException("Nothing captured yet");

        var first = chosen[0].Start;
        // Keyframes sit on whole seconds from the start of a segment (GOP = 1 s), so cut on a whole
        // second: a stream copy can only begin on a keyframe, and anything else leaves audio with no picture.
        double offset = Math.Floor(Math.Max(0, (from - first).TotalSeconds));
        double duration = Math.Max(0.5, (to - first).TotalSeconds - offset);
        if (offset > 0) offset -= 0.03;

        // TS segments are byte-concatenable, so join them physically: timestamps stay continuous.
        var listName = $"export_{Guid.NewGuid():N}.txt";
        var listPath = Path.Combine(dir, listName);
        await File.WriteAllLinesAsync(listPath, chosen.Select(c => Path.GetFileName(c.Path)));

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("-hide_banner -loglevel error -y ");
        if (offset > 0.05) sb.Append($"-ss {offset.ToString("0.###", inv)} ");
        sb.Append($"-i \"concatf:{listName}\" -t {duration.ToString("0.###", inv)} -map 0 -c copy -movflags +faststart ");
        for (int i = 0; i < _trackTitles.Count; i++)
            sb.Append($"-metadata:s:a:{i} title=\"{_trackTitles[i]}\" -metadata:s:a:{i} handler_name=\"{_trackTitles[i]}\" ");
        sb.Append($"\"{outPath}\"");

        try
        {
            var (code, err) = await FfmpegRunner.RunAsync(ffmpeg, sb.ToString(), dir, 10 * 60 * 1000);
            if (code != 0 || !File.Exists(outPath))
            {
                Log.Write($"Export failed ({code}): {err}");
                throw new InvalidOperationException("Export failed: " + err.Trim().Split('\n').LastOrDefault());
            }
        }
        finally { try { File.Delete(listPath); } catch { } }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(6000); } catch { }
    }
}
