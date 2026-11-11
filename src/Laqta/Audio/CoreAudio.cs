using System.Runtime.InteropServices;

namespace Laqta.Audio;

// Minimal hand-written WASAPI interop: just what is needed for microphone capture
// and per-process loopback capture (Windows 10 2004+).

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FmtId;
    public int Pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort R1, R2, R3;
    public IntPtr P1;
    public IntPtr P2;
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal interface IAgileObject { }

internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Done = new(false);
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        Done.Set();
        return 0;
    }
}

public sealed record AudioDevice(string Id, string Name);

internal static class CoreAudio
{
    public const int SampleRate = 48000;
    public const int BytesPerFrame = 4; // 16-bit stereo

    public const int FlagLoopback = 0x00020000;
    public const int FlagEventCallback = 0x00040000;
    public const int FlagAutoConvertPcm = unchecked((int)0x80000000);
    public const int FlagSrcDefaultQuality = 0x08000000;
    public const int BufferFlagSilent = 0x2;

    public static Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private static PropertyKey _friendlyName = new() { FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    /// <summary>WAVEFORMATEX for 48 kHz / 16-bit / stereo PCM. Caller frees with FreeHGlobal.</summary>
    public static IntPtr AllocFormat()
    {
        var p = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(p, 0, 1);
        Marshal.WriteInt16(p, 2, 2);
        Marshal.WriteInt32(p, 4, SampleRate);
        Marshal.WriteInt32(p, 8, SampleRate * BytesPerFrame);
        Marshal.WriteInt16(p, 12, BytesPerFrame);
        Marshal.WriteInt16(p, 14, 16);
        Marshal.WriteInt16(p, 16, 0);
        return p;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    /// <summary>Activates a loopback client that only hears the given process and its children.</summary>
    public static IAudioClient ActivateProcessLoopback(int pid)
    {
        var blob = Marshal.AllocHGlobal(12);
        var pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(blob, 0, 1);   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(blob, 4, pid); // TargetProcessId
            Marshal.WriteInt32(blob, 8, 0);   // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);    // VT_BLOB
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, 16, blob);

            var handler = new ActivationHandler();
            var iid = IidAudioClient;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out var op), "ActivateAudioInterfaceAsync");
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Process loopback activation timed out");
            Check(op.GetActivateResult(out int result, out object unk), "GetActivateResult");
            Check(result, "Process loopback activation");
            GC.KeepAlive(handler);
            return (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>Activates a capture client on a microphone. Empty id = default device.</summary>
    public static IAudioClient ActivateMicrophone(string deviceId)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        if (!string.IsNullOrEmpty(deviceId) && enumerator.GetDevice(deviceId, out device) < 0) device = null;
        if (device == null) Check(enumerator.GetDefaultAudioEndpoint(1 /*eCapture*/, 0 /*eConsole*/, out device), "GetDefaultAudioEndpoint");
        var iid = IidAudioClient;
        Check(device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object unk), "IMMDevice.Activate");
        return (IAudioClient)unk;
    }

    public static List<AudioDevice> ListMicrophones()
    {
        var list = new List<AudioDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.EnumAudioEndpoints(1, 1 /*ACTIVE*/, out var col) < 0) return list;
            col.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) < 0) continue;
                dev.GetId(out string id);
                string name = id;
                if (dev.OpenPropertyStore(0, out var store) >= 0 && store.GetValue(ref _friendlyName, out var pv) >= 0)
                {
                    if (pv.Vt == 31 && pv.P1 != IntPtr.Zero) name = Marshal.PtrToStringUni(pv.P1) ?? id;
                    PropVariantClear(ref pv);
                }
                list.Add(new AudioDevice(id, name));
            }
        }
        catch { }
        return list;
    }
}
