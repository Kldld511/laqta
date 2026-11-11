using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Laqta;

/// <summary>Small click-through, never-focused notification in the corner of the screen.</summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public enum Tone { Accent, Good, Record }

    private ToastWindow(string glyph, string title, string? sub, Tone tone)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        IconText.Text = glyph;
        TitleText.Text = title;
        SubText.Text = sub ?? "";
        SubText.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        var (bg, fg) = tone switch
        {
            Tone.Good => ("MintSoftBrush", "MintBrush"),
            Tone.Record => ("RedSoftBrush", "RedBrush"),
            _ => ("AccentSoftBrush", "AccentHoverBrush"),
        };
        IconBg.Background = (Brush)FindResource(bg);
        IconText.Foreground = (Brush)FindResource(fg);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Top;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
                fade.Completed += (_, _) => { if (_current == this) _current = null; Close(); };
                BeginAnimation(OpacityProperty, fade);
            };
            timer.Start();
        };
    }

    public static void Show(string glyph, string title, string? sub = null, Tone tone = Tone.Accent)
    {
        try
        {
            _current?.Close();
            _current = new ToastWindow(glyph, title, sub, tone);
            _current.Show();
        }
        catch { }
    }
}
