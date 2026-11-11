using System.Runtime.InteropServices;

namespace Laqta.Core;

public sealed record DisplayInfo(int Index, string DeviceName, int X, int Y, int Width, int Height)
{
    public string Label => $"{Index + 1}  ·  {Width}×{Height}";
}

/// <summary>
/// Lists monitors in DXGI output order of the default adapter, which is the same numbering
/// FFmpeg's ddagrab uses for output_idx.
/// </summary>
public static class Displays
{
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
    private interface IDXGIFactory
    {
        [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetParent(IntPtr riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
    private interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetParent(IntPtr riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
    private interface IDXGIOutput
    {
        [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetParent(IntPtr riid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    public static List<DisplayInfo> List()
    {
        var list = new List<DisplayInfo>();
        try
        {
            var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            if (CreateDXGIFactory1(ref iid, out object obj) >= 0)
            {
                var factory = (IDXGIFactory)obj;
                if (factory.EnumAdapters(0, out var adapter) >= 0)
                {
                    for (uint i = 0; adapter.EnumOutputs(i, out var output) >= 0; i++)
                    {
                        if (output.GetDesc(out var d) >= 0 && d.AttachedToDesktop != 0)
                            list.Add(new DisplayInfo((int)i, d.DeviceName, d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top));
                        Marshal.ReleaseComObject(output);
                    }
                    Marshal.ReleaseComObject(adapter);
                }
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) { Log.Write("DXGI enumeration failed: " + ex.Message); }

        if (list.Count == 0)
        {
            int i = 0;
            foreach (var s in System.Windows.Forms.Screen.AllScreens.OrderByDescending(s => s.Primary))
                list.Add(new DisplayInfo(i++, s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
        }
        return list;
    }

    public static DisplayInfo Get(int index)
    {
        var all = List();
        return all.FirstOrDefault(d => d.Index == index) ?? all[0];
    }
}

public static class Screenshot
{
    public static string Capture(DisplayInfo display, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"Laqta_Shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(outputDir, $"Laqta_Shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{n}.png");
        using var bmp = new System.Drawing.Bitmap(display.Width, display.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(display.X, display.Y, 0, 0, new System.Drawing.Size(display.Width, display.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }
}
