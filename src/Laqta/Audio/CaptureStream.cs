using System.Runtime.InteropServices;

namespace Laqta.Audio;

/// <summary>Thread-safe byte ring buffer. When full, the oldest audio is dropped.</summary>
internal sealed class RingBuffer
{
    private readonly byte[] _buf;
    private int _read, _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity) => _buf = new byte[capacity];

    public int Available { get { lock (_lock) return _count; } }

    public void Clear() { lock (_lock) { _read = 0; _count = 0; } }

    public unsafe void Write(IntPtr src, int bytes, bool silence)
    {
        lock (_lock)
        {
            if (bytes > _buf.Length) { if (!silence) src += bytes - _buf.Length; bytes = _buf.Length; }
            int overflow = _count + bytes - _buf.Length;
            if (overflow > 0) { _read = (_read + overflow) % _buf.Length; _count -= overflow; }
            int w = (_read + _count) % _buf.Length;
            int first = Math.Min(bytes, _buf.Length - w);
            if (silence)
            {
                Array.Clear(_buf, w, first);
                Array.Clear(_buf, 0, bytes - first);
            }
            else
            {
                Marshal.Copy(src, _buf, w, first);
                if (bytes > first) Marshal.Copy(src + first, _buf, 0, bytes - first);
            }
            _count += bytes;
        }
    }

    public int Read(byte[] dst, int bytes)
    {
        lock (_lock)
        {
            bytes = Math.Min(bytes, _count);
            int first = Math.Min(bytes, _buf.Length - _read);
            Buffer.BlockCopy(_buf, _read, dst, 0, first);
            if (bytes > first) Buffer.BlockCopy(_buf, 0, dst, first, bytes - first);
            _read = (_read + bytes) % _buf.Length;
            _count -= bytes;
            return bytes;
        }
    }

    public void Skip(int bytes)
    {
        lock (_lock)
        {
            bytes = Math.Min(bytes, _count);
            _read = (_read + bytes) % _buf.Length;
            _count -= bytes;
        }
    }
}

/// <summary>
/// One WASAPI capture (a microphone or a process-loopback) running on its own thread and
/// pushing 48 kHz 16-bit stereo PCM into a ring buffer.
/// </summary>
internal sealed class CaptureStream : IDisposable
{
    private readonly Func<IAudioClient> _activate;
    private readonly bool _loopback;
    private readonly Thread _thread;
    private volatile bool _stop;

    public RingBuffer Ring { get; } = new(CoreAudio.SampleRate * CoreAudio.BytesPerFrame); // 1 second
    public volatile bool Failed;
    public string? Error;

    public CaptureStream(Func<IAudioClient> activate, bool loopback)
    {
        _activate = activate;
        _loopback = loopback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Laqta audio capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var format = CoreAudio.AllocFormat();
        using var evt = new AutoResetEvent(false);
        try
        {
            client = _activate();
            int flags = CoreAudio.FlagEventCallback | CoreAudio.FlagAutoConvertPcm | CoreAudio.FlagSrcDefaultQuality;
            if (_loopback) flags |= CoreAudio.FlagLoopback;
            CoreAudio.Check(client.Initialize(0, flags, 200000, 0, format, IntPtr.Zero), "IAudioClient.Initialize");
            CoreAudio.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = CoreAudio.IidAudioCaptureClient;
            CoreAudio.Check(client.GetService(ref iid, out object svc), "GetService");
            capture = (IAudioCaptureClient)svc;
            CoreAudio.Check(client.Start(), "IAudioClient.Start");

            while (!_stop)
            {
                evt.WaitOne(100);
                while (!_stop)
                {
                    CoreAudio.Check(capture.GetNextPacketSize(out uint next), "GetNextPacketSize");
                    if (next == 0) break;
                    CoreAudio.Check(capture.GetBuffer(out IntPtr data, out uint frames, out int bufFlags, out _, out _), "GetBuffer");
                    if (frames > 0)
                        Ring.Write(data, (int)frames * CoreAudio.BytesPerFrame, (bufFlags & CoreAudio.BufferFlagSilent) != 0);
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Failed = true;
        }
        finally
        {
            Marshal.FreeHGlobal(format);
            try { if (capture != null) Marshal.ReleaseComObject(capture); } catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }
}
