using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Laqta.Core;

namespace Laqta.Audio;

public enum TrackKind { Game, Mic, Discord, Browser }

internal sealed class Feed : IDisposable
{
    public required string Key;
    public required string Name;
    public required CaptureStream Stream;
    public bool Primed;
    public void Dispose() => Stream.Dispose();
}

/// <summary>One output audio track. It can be fed by several captures (e.g. two browsers) that are mixed together.</summary>
public sealed class AudioTrack
{
    private const int PrebufferFrames = CoreAudio.SampleRate * 30 / 1000;   // 30 ms cushion against jitter
    private const int MaxLatencyFrames = CoreAudio.SampleRate * 250 / 1000; // drop backlog beyond 250 ms

    public TrackKind Kind { get; }
    public TrackSettings Settings { get; }
    public string EnglishName => Kind switch
    {
        TrackKind.Game => "Game",
        TrackKind.Mic => "Microphone",
        TrackKind.Discord => "Discord",
        _ => "Browser",
    };

    /// <summary>Peak level (0..1) since the UI last read it.</summary>
    public float Peak;
    public volatile string SourceLabel = "";

    internal volatile Feed[] Feeds = Array.Empty<Feed>();
    private byte[] _bytes = new byte[4096];
    private int[] _acc = new int[2048];

    internal AudioTrack(TrackKind kind, TrackSettings settings) { Kind = kind; Settings = settings; }

    internal void ResetBuffers()
    {
        foreach (var f in Feeds) { f.Stream.Ring.Clear(); f.Primed = false; }
    }

    /// <summary>Fills dst with exactly `frames` stereo frames, padding with silence when a source has nothing to say.</summary>
    internal void Pull(short[] dst, int frames)
    {
        int samples = frames * 2, bytes = frames * CoreAudio.BytesPerFrame;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        if (_acc.Length < samples) _acc = new int[samples];
        Array.Clear(_acc, 0, samples);

        foreach (var f in Feeds)
        {
            var ring = f.Stream.Ring;
            int avail = ring.Available / CoreAudio.BytesPerFrame;
            if (!f.Primed)
            {
                if (avail < PrebufferFrames) continue;
                f.Primed = true;
            }
            if (avail > MaxLatencyFrames) ring.Skip((avail - PrebufferFrames) * CoreAudio.BytesPerFrame);
            int got = ring.Read(_bytes, bytes);
            if (got < bytes) f.Primed = false;
            var src = MemoryMarshal.Cast<byte, short>(_bytes.AsSpan(0, got - got % 2));
            for (int i = 0; i < src.Length; i++) _acc[i] += src[i];
        }

        float gain = (float)Settings.Gain;
        int peak = 0;
        for (int i = 0; i < samples; i++)
        {
            int v = (int)(_acc[i] * gain);
            if (v > short.MaxValue) v = short.MaxValue; else if (v < short.MinValue) v = short.MinValue;
            dst[i] = (short)v;
            int a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        float p = peak / 32768f;
        if (p > Peak) Peak = p;
    }
}

/// <summary>Owns the four tracks, keeps their captures attached to the right processes, and feeds FFmpeg.</summary>
public sealed class AudioEngine : IDisposable
{
    private readonly AppSettings _settings;
    private Timer? _supervisor;
    private int _ticking;
    private string _micDevice = "";

    public IReadOnlyList<AudioTrack> Tracks { get; }

    public AudioEngine(AppSettings settings)
    {
        _settings = settings;
        Tracks = new[]
        {
            new AudioTrack(TrackKind.Game, settings.Game),
            new AudioTrack(TrackKind.Mic, settings.Mic),
            new AudioTrack(TrackKind.Discord, settings.Discord),
            new AudioTrack(TrackKind.Browser, settings.Browser),
        };
    }

    public List<AudioTrack> EnabledTracks() => Tracks.Where(t => t.Settings.Enabled).ToList();

    public void Start()
    {
        _supervisor ??= new Timer(_ => Tick(), null, 0, 1000);
    }

    public void Stop()
    {
        _supervisor?.Dispose();
        _supervisor = null;
        SpinWait.SpinUntil(() => Volatile.Read(ref _ticking) == 0, 2000);
        foreach (var t in Tracks)
        {
            var feeds = t.Feeds;
            t.Feeds = Array.Empty<Feed>();
            foreach (var f in feeds) f.Dispose();
            t.SourceLabel = "";
            t.Peak = 0;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            Dictionary<int, ProcessResolver.ProcInfo>? procs = null;
            foreach (var track in Tracks)
            {
                var wanted = new List<(string Key, string Name, Func<IAudioClient> Activate, bool Loopback)>();
                if (track.Settings.Enabled && _supervisor != null)
                {
                    if (track.Kind == TrackKind.Mic)
                    {
                        string dev = track.Settings.Target ?? "";
                        wanted.Add(("mic:" + dev, "", () => CoreAudio.ActivateMicrophone(dev), false));
                    }
                    else
                    {
                        procs ??= ProcessResolver.Snapshot();
                        foreach (var p in ProcessResolver.Resolve(track.Kind, track.Settings.Target ?? "", procs))
                        {
                            int pid = p.Pid;
                            wanted.Add(("pid:" + pid, p.Name, () => CoreAudio.ActivateProcessLoopback(pid), true));
                        }
                    }
                }

                var current = track.Feeds;
                var next = new List<Feed>();
                bool changed = false;
                foreach (var f in current)
                {
                    if (!f.Stream.Failed && wanted.Any(w => w.Key == f.Key)) next.Add(f);
                    else
                    {
                        if (f.Stream.Failed) Log.Write($"Audio capture failed for {track.Kind} ({f.Name}): {f.Stream.Error}");
                        changed = true;
                    }
                }
                foreach (var w in wanted)
                {
                    if (next.Any(f => f.Key == w.Key)) continue;
                    next.Add(new Feed { Key = w.Key, Name = w.Name, Stream = new CaptureStream(w.Activate, w.Loopback) });
                    changed = true;
                }
                if (changed)
                {
                    track.Feeds = next.ToArray();
                    foreach (var f in current) if (!next.Contains(f)) f.Dispose();
                }
                track.SourceLabel = string.Join(" + ", next.Select(f => f.Name).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { Log.Write("Audio supervisor: " + ex); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>Opens a named pipe that streams all given tracks as one interleaved multichannel PCM signal.</summary>
    internal AudioPipe OpenPipe(List<AudioTrack> tracks) => new(tracks);

    public void Dispose() => Stop();
}

/// <summary>
/// Clock-driven writer: every track contributes two channels to one interleaved s16le stream, so
/// all tracks stay sample-locked to each other. FFmpeg splits the channels back into tracks.
/// </summary>
internal sealed class AudioPipe : IDisposable
{
    private const int ChunkFrames = 480; // 10 ms
    private readonly List<AudioTrack> _tracks;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public string Name { get; } = "laqta_audio_" + Guid.NewGuid().ToString("N");
    public string FullPath => @"\\.\pipe\" + Name;
    public int Channels => _tracks.Count * 2;

    public AudioPipe(List<AudioTrack> tracks)
    {
        _tracks = tracks;
        _pipe = new NamedPipeServerStream(Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _thread = new Thread(Run) { IsBackground = true, Name = "Laqta audio mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
            foreach (var t in _tracks) t.ResetBuffers();

            int n = _tracks.Count;
            var one = new short[ChunkFrames * 2];
            var mixed = new short[ChunkFrames * 2 * n];
            var bytes = new byte[mixed.Length * 2];
            var clock = Stopwatch.StartNew();
            long written = 0;

            while (!_cts.IsCancellationRequested)
            {
                long target = clock.ElapsedTicks * CoreAudio.SampleRate / Stopwatch.Frequency;
                while (target - written >= ChunkFrames && !_cts.IsCancellationRequested)
                {
                    for (int t = 0; t < n; t++)
                    {
                        _tracks[t].Pull(one, ChunkFrames);
                        int ch = t * 2, stride = n * 2;
                        for (int i = 0; i < ChunkFrames; i++)
                        {
                            mixed[i * stride + ch] = one[i * 2];
                            mixed[i * stride + ch + 1] = one[i * 2 + 1];
                        }
                    }
                    Buffer.BlockCopy(mixed, 0, bytes, 0, bytes.Length);
                    _pipe.Write(bytes, 0, bytes.Length);
                    written += ChunkFrames;
                }
                Thread.Sleep(4);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // FFmpeg closed its end
        catch (Exception ex) { Log.Write("Audio pipe: " + ex.Message); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pipe.Dispose(); } catch { }
        if (Thread.CurrentThread != _thread) _thread.Join(1500);
    }
}
