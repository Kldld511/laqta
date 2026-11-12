using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Laqta.Audio;
using Laqta.Core;

namespace Laqta;

public partial class MainWindow : Window
{
    private readonly AppSettings _s;
    private readonly AudioEngine _audio;
    private readonly CaptureEngine _engine;
    private readonly HotkeyManager _hotkeys = new();
    private readonly ObservableCollection<TrackVM> _tracks = new();
    private readonly ObservableCollection<MediaItem> _media = new();
    private readonly DispatcherTimer _uiTimer, _restartTimer, _saveTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _loading = true, _exiting, _trayHintShown, _savingReplay, _togglingRecord;
    private string? _error;
    private Button? _capturingHotkey;
    private int _galleryVersion;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow(AppSettings settings)
    {
        _s = settings;
        _audio = new AudioEngine(_s);
        _engine = new CaptureEngine(_s, _audio);
        InitializeComponent();
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        foreach (var t in _audio.Tracks)
        {
            var vm = new TrackVM(t, restart => { SaveSoon(); if (restart) ScheduleRestart(); });
            vm.RefreshOptions();
            _tracks.Add(vm);
        }
        TrackList.ItemsSource = _tracks;
        MediaList.ItemsSource = _media;

        BuildCombos();
        ReplayToggle.IsChecked = _s.ReplayEnabled;
        CursorToggle.IsChecked = _s.CaptureCursor;
        MixToggle.IsChecked = _s.IncludeMix;
        TrayToggle.IsChecked = _s.MinimizeToTray;
        SoundToggle.IsChecked = _s.Sounds;
        Sounds.Enabled = _s.Sounds;
        OutputDirText.Text = _s.OutputDir;
        _loading = false;

        _engine.StateChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        _engine.Faulted += msg => Dispatcher.BeginInvoke(() => { _error = msg; UpdateStatus(); });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _restartTimer.Tick += async (_, _) => { _restartTimer.Stop(); await RestartEngineAsync(); };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _s.Save(); };

        SourceInitialized += (_, _) =>
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        };
        StateChanged += (_, _) =>
        {
            RootBorder.Padding = new Thickness(WindowState == WindowState.Maximized ? 7 : 0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        ApplyHotkeys();
        BuildTray();
        UpdateStatus();
        if (_s.ReplayEnabled) _ = StartEngineAsync();
    }

    // ---------------------------------------------------------------- setup

    private static void Fill(ComboBox combo, IEnumerable<Option> options, string selected)
    {
        var list = options.ToList();
        combo.ItemsSource = list;
        combo.SelectedItem = list.FirstOrDefault(o => o.Value == selected) ?? list.FirstOrDefault();
    }

    private void BuildCombos()
    {
        bool was = _loading;
        _loading = true;
        Fill(ReplayLenCombo, new[]
        {
            new Option("30", Loc.T("S_Sec30")), new Option("60", Loc.T("S_Min1")), new Option("120", Loc.T("S_Min2")),
            new Option("180", Loc.T("S_Min3")), new Option("300", Loc.T("S_Min5")), new Option("600", Loc.T("S_Min10")),
        }, _s.ReplaySeconds.ToString());
        Fill(MonitorCombo, Displays.List().Select(d => new Option(d.Index.ToString(), d.Label)), _s.Monitor.ToString());
        Fill(FpsCombo, new[] { new Option("30", "30"), new Option("60", "60"), new Option("120", "120") }, _s.Fps.ToString());
        Fill(QualityCombo, new[]
        {
            new Option("Low", Loc.T("S_QLow")), new Option("Medium", Loc.T("S_QMedium")),
            new Option("High", Loc.T("S_QHigh")), new Option("Ultra", Loc.T("S_QUltra")),
        }, _s.Quality);
        Fill(LangCombo, new[] { new Option("ar", "العربية"), new Option("en", "English") }, _s.Language);
        _loading = was;
    }

    private void ApplyHotkeys()
    {
        bool ok = _hotkeys.Register(1, _s.HotkeyRecord, () => _ = ToggleRecordAsync());
        ok &= _hotkeys.Register(2, _s.HotkeyReplay, () => _ = SaveReplayAsync());
        ok &= _hotkeys.Register(3, _s.HotkeyScreenshot, () => _ = TakeScreenshotAsync());
        HotkeyWarn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        HkRecordBtn.Content = HkRecordChip.Text = _s.HotkeyRecord;
        HkReplayBtn.Content = HkReplayChip.Text = _s.HotkeyReplay;
        HkShotBtn.Content = HkShotChip.Text = _s.HotkeyScreenshot;
    }

    private void BuildTray()
    {
        _tray ??= new System.Windows.Forms.NotifyIcon { Text = "Laqta", Visible = true };
        try { _tray.Icon ??= System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("S_TrayShow"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Loc.T("S_SaveReplay"), null, (_, _) => _ = SaveReplayAsync());
        menu.Items.Add(Loc.T("S_Screenshot"), null, (_, _) => _ = TakeScreenshotAsync());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("S_TrayExit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick -= TrayDoubleClick;
        _tray.DoubleClick += TrayDoubleClick;
    }

    private void TrayDoubleClick(object? sender, EventArgs e) => ShowFromTray();

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- engine

    private async Task StartEngineAsync()
    {
        try
        {
            _error = null;
            await _engine.StartAsync();
        }
        catch (FileNotFoundException) { _error = Loc.T("S_NoFfmpeg"); }
        catch (Exception ex) { _error = ex.Message; Log.Write("Start failed: " + ex); }
        UpdateStatus();
    }

    private async Task RestartEngineAsync()
    {
        try
        {
            _error = null;
            await _engine.RestartAsync();
        }
        catch (Exception ex) { _error = ex.Message; Log.Write("Restart failed: " + ex); }
        UpdateStatus();
    }

    private void ScheduleRestart()
    {
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task ToggleRecordAsync()
    {
        if (_togglingRecord) return;
        _togglingRecord = true;
        try
        {
            _error = null;
            if (_engine.IsRecording)
            {
                Sounds.PlayStop();
                ToastWindow.Show("", Loc.T("S_ToastRecSaving"));
                var path = await _engine.StopRecordingAsync();
                if (path != null) ToastWindow.Show("", Loc.T("S_ToastRecSaved"), Path.GetFileName(path), ToastWindow.Tone.Good);
                RefreshGalleryIfVisible();
            }
            else
            {
                await _engine.StartRecordingAsync();
                Sounds.PlayStart();
                ToastWindow.Show("", Loc.T("S_ToastRecStarted"), null, ToastWindow.Tone.Record);
            }
        }
        catch (FileNotFoundException) { _error = Loc.T("S_NoFfmpeg"); ToastWindow.Show("", Loc.T("S_Error"), null, ToastWindow.Tone.Record); }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Write("Record toggle failed: " + ex);
            ToastWindow.Show("", Loc.T("S_Error"), null, ToastWindow.Tone.Record);
        }
        finally { _togglingRecord = false; UpdateStatus(); }
    }

    private async Task SaveReplayAsync()
    {
        if (_savingReplay) return;
        if (!_engine.IsRunning)
        {
            ToastWindow.Show("", Loc.T("S_ToastReplayOff"));
            return;
        }
        _savingReplay = true;
        try
        {
            ToastWindow.Show("", Loc.T("S_ToastReplaySaving"));
            var path = await _engine.SaveReplayAsync();
            if (path != null) Sounds.PlaySaved();
            if (path != null) ToastWindow.Show("", Loc.T("S_ToastReplaySaved"), Path.GetFileName(path), ToastWindow.Tone.Good);
            RefreshGalleryIfVisible();
        }
        catch (Exception ex)
        {
            Log.Write("Replay save failed: " + ex);
            ToastWindow.Show("", Loc.T("S_Error"), null, ToastWindow.Tone.Record);
        }
        finally { _savingReplay = false; }
    }

    private async Task TakeScreenshotAsync()
    {
        try
        {
            var path = await Task.Run(() => Screenshot.Capture(Displays.Get(_s.Monitor), _s.OutputDir));
            Sounds.PlayShutter();
            ToastWindow.Show("", Loc.T("S_ToastShotSaved"), Path.GetFileName(path), ToastWindow.Tone.Good);
            RefreshGalleryIfVisible();
        }
        catch (Exception ex)
        {
            Log.Write("Screenshot failed: " + ex);
            ToastWindow.Show("", Loc.T("S_Error"), null, ToastWindow.Tone.Record);
        }
    }

    // ---------------------------------------------------------------- status + meters

    private void UpdateStatus()
    {
        bool rec = _engine.IsRecording, running = _engine.IsRunning;
        string dot, halo, title, sub;
        if (_error != null && !running) { dot = "RedBrush"; halo = "RedSoftBrush"; title = Loc.T("S_Error"); sub = _error; }
        else if (_engine.IsStarting) { dot = "AccentBrush"; halo = "AccentSoftBrush"; title = Loc.T("S_Starting"); sub = Loc.T("S_StartingSub"); }
        else if (rec) { dot = "RedBrush"; halo = "RedSoftBrush"; title = Loc.T("S_Recording"); sub = Loc.T("S_RecordingSub"); }
        else if (running)
        {
            dot = "MintBrush"; halo = "MintSoftBrush"; title = Loc.T("S_ReplayOn");
            sub = string.Format(Loc.T("S_ReplayOnSub"), (ReplayLenCombo.SelectedItem as Option)?.Label ?? "");
        }
        else { dot = "MutedBrush"; halo = "AccentSoftBrush"; title = Loc.T("S_Ready"); sub = Loc.T("S_ReadySub"); }

        StatusDot.Fill = (Brush)FindResource(dot);
        StatusHalo.Background = (Brush)FindResource(halo);
        StatusTitle.Text = title;
        StatusSub.Text = sub;
        TimerText.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        RecordLabel.Text = Loc.T(rec ? "S_StopRecord" : "S_Record");
        RecordIcon.Text = rec ? "" : "";
        RecordIconBg.Background = (Brush)FindResource(rec ? "RedBrush" : "RedSoftBrush");
        RecordIcon.Foreground = rec ? Brushes.White : (Brush)FindResource("RedBrush");
        ReplayBtn.IsEnabled = running;
        EncoderText.Text = _engine.Encoder?.Label ?? "—";
        EncoderDot.Fill = (Brush)FindResource(_engine.Encoder != null ? "MintBrush" : "MutedBrush");
        if (_tray != null) _tray.Text = rec ? "Laqta — REC" : "Laqta";
    }

    private void UiTick()
    {
        bool running = _engine.IsRunning;
        foreach (var vm in _tracks)
        {
            var t = vm.Track;
            float peak = t.Peak;
            t.Peak = 0;
            double shown = running && t.Settings.Enabled ? Math.Sqrt(Math.Min(1f, peak)) : 0;
            vm.Level = Math.Max(shown, vm.Level * 0.82);

            if (!t.Settings.Enabled) vm.Source = Loc.T("S_Off");
            else if (!running) vm.Source = Loc.T("S_Idle");
            else if (t.Kind == TrackKind.Mic) vm.Source = vm.Selected?.Label ?? "";
            else vm.Source = t.SourceLabel.Length > 0 ? t.SourceLabel : Loc.T("S_Waiting");
        }
        if (_engine.IsRecording)
        {
            var e = DateTime.UtcNow - _engine.RecordStartUtc;
            TimerText.Text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
        }
    }

    // ---------------------------------------------------------------- home page events

    private void Record_Click(object sender, RoutedEventArgs e) => _ = ToggleRecordAsync();
    private void SaveReplay_Click(object sender, RoutedEventArgs e) => _ = SaveReplayAsync();
    private void Screenshot_Click(object sender, RoutedEventArgs e) => _ = TakeScreenshotAsync();

    private void ReplayToggle_Click(object sender, RoutedEventArgs e)
    {
        _s.ReplayEnabled = ReplayToggle.IsChecked == true;
        _s.Save();
        if (_s.ReplayEnabled) _ = StartEngineAsync();
        else if (!_engine.IsRecording) _ = _engine.StopAsync();
    }

    private void ReplayLen_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ReplayLenCombo.SelectedItem is not Option o) return;
        _s.ReplaySeconds = int.Parse(o.Value);
        _s.Save();
        UpdateStatus();
    }

    private void TrackCombo_DropDownOpened(object sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackVM vm) vm.RefreshOptions();
    }

    // ---------------------------------------------------------------- settings page events

    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MonitorCombo.SelectedItem is Option m && int.Parse(m.Value) != _s.Monitor)
        {
            _s.Monitor = int.Parse(m.Value);
            _engine.InvalidateEncoder();
        }
        if (FpsCombo.SelectedItem is Option f) _s.Fps = int.Parse(f.Value);
        if (QualityCombo.SelectedItem is Option q) _s.Quality = q.Value;
        _s.Save();
        ScheduleRestart();
    }

    private void Setting_Clicked(object sender, RoutedEventArgs e)
    {
        _s.CaptureCursor = CursorToggle.IsChecked == true;
        _s.IncludeMix = MixToggle.IsChecked == true;
        _s.MinimizeToTray = TrayToggle.IsChecked == true;
        _s.Sounds = Sounds.Enabled = SoundToggle.IsChecked == true;
        _s.Save();
        if (sender == SoundToggle) Sounds.PlaySaved();
        if (sender != TrayToggle && sender != SoundToggle) ScheduleRestart();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        try { if (Directory.Exists(_s.OutputDir)) dlg.InitialDirectory = _s.OutputDir; } catch { }
        if (dlg.ShowDialog(this) == true)
        {
            _s.OutputDir = dlg.FolderName;
            OutputDirText.Text = _s.OutputDir;
            _s.Save();
        }
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangCombo.SelectedItem is not Option o || o.Value == _s.Language) return;
        _s.Language = o.Value;
        _s.Save();
        Loc.Apply(_s.Language);
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        BuildCombos();
        foreach (var t in _tracks) t.RefreshTexts();
        BuildTray();
        UpdateStatus();
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        // Release the global hotkeys so the new combination reaches this window.
        for (int id = 1; id <= 3; id++) _hotkeys.Register(id, "", () => { });
        _capturingHotkey = (Button)sender;
        _capturingHotkey.Content = Loc.T("S_PressKeys");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingHotkey != null)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;
            if (key != Key.Escape)
            {
                var gesture = HotkeyManager.Format(Keyboard.Modifiers, key);
                switch (_capturingHotkey.Tag as string)
                {
                    case "Record": _s.HotkeyRecord = gesture; break;
                    case "Replay": _s.HotkeyReplay = gesture; break;
                    default: _s.HotkeyScreenshot = gesture; break;
                }
                _s.Save();
            }
            _capturingHotkey = null;
            ApplyHotkeys();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------------------------------------------------------------- navigation + gallery

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage == null || GalleryPage == null || SettingsPage == null) return;
        HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GalleryPage.Visibility = NavGallery.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavGallery.IsChecked == true) RefreshGallery();
    }

    private void RefreshGalleryIfVisible()
    {
        if (GalleryPage.Visibility == Visibility.Visible) RefreshGallery();
    }

    private void RefreshGallery()
    {
        int version = ++_galleryVersion;
        _media.Clear();
        try
        {
            if (Directory.Exists(_s.OutputDir))
            {
                var files = new DirectoryInfo(_s.OutputDir).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime).Take(80);
                foreach (var f in files)
                {
                    double mb = f.Length / 1048576.0;
                    _media.Add(new MediaItem
                    {
                        Path = f.FullName,
                        Name = f.Name,
                        IsVideo = f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
                        Info = $"{f.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {(mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0.0} MB")}",
                    });
                }
            }
        }
        catch (Exception ex) { Log.Write("Gallery: " + ex.Message); }
        EmptyState.Visibility = _media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var items = _media.ToList();
        Task.Run(async () =>
        {
            foreach (var item in items)
            {
                if (version != _galleryVersion) return;
                var img = await LoadThumbAsync(item);
                if (img != null) _ = Dispatcher.BeginInvoke(() => item.Thumb = img);
            }
        });
    }

    private static async Task<ImageSource?> LoadThumbAsync(MediaItem item)
    {
        try
        {
            string source = item.Path;
            if (item.IsVideo)
            {
                var info = new FileInfo(item.Path);
                var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(item.Path + info.LastWriteTimeUtc.Ticks)));
                source = Path.Combine(AppPaths.ThumbDir, hash + ".jpg");
                if (!File.Exists(source))
                {
                    var ffmpeg = FfmpegLocator.Find();
                    if (ffmpeg == null) return null;
                    Directory.CreateDirectory(AppPaths.ThumbDir);
                    await FfmpegRunner.RunAsync(ffmpeg,
                        $"-hide_banner -loglevel error -y -ss 0.5 -i \"{item.Path}\" -frames:v 1 -vf scale=456:-2 -q:v 4 \"{source}\"", null, 15000);
                    if (!File.Exists(source)) return null;
                }
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 456;
            bmp.UriSource = new Uri(source);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private void Media_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem m) OpenPath(m.Path);
    }

    private void MediaOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem m) OpenPath(m.Path);
    }

    private void MediaReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem m)
            try { Process.Start("explorer.exe", $"/select,\"{m.Path}\""); } catch { }
    }

    private void MediaDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaItem m) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(m.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            _media.Remove(m);
            EmptyState.Visibility = _media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Write("Delete failed: " + ex.Message); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_s.OutputDir); } catch { }
        OpenPath(_s.OutputDir);
    }

    private const string SupportUrl = "https://creators.sa/nsun7779";

    private void Support_Click(object sender, RoutedEventArgs e) => OpenPath(SupportUrl);

    /// <summary>Renders every page of the window to PNG files (used for docs and UI checks), then exits.</summary>
    public async Task SnapshotAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        void Save(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(RootBorder.ActualWidth * dpi.DpiScaleX), (int)(RootBorder.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(RootBorder);
            // Rendering a child visual skips the window's right-to-left mirror, so re-apply it.
            BitmapSource image = Loc.IsRtl ? new TransformedBitmap(rtb, new ScaleTransform(-1, 1)) : rtb;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        await Task.Delay(6000);
        var original = _s.Language;
        foreach (var lang in new[] { original, original == "ar" ? "en" : "ar" })
        {
            LangCombo.SelectedItem = ((List<Option>)LangCombo.ItemsSource).First(o => o.Value == lang);
            NavHome.IsChecked = true;
            await Task.Delay(700);
            Save("home-" + lang);
            NavGallery.IsChecked = true;
            await Task.Delay(3000);
            Save("gallery-" + lang);
            NavSettings.IsChecked = true;
            await Task.Delay(500);
            Save("settings-" + lang);
        }
        _s.Language = original;
        _exiting = true;
        Cleanup();
    }

    // ---------------------------------------------------------------- window chrome + lifetime

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _s.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ToastWindow.Show("", Loc.T("S_ToastTray"));
            }
            return;
        }
        base.OnClosing(e);
        if (!_exiting) Cleanup();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        Cleanup();
        Close();
    }

    private void Cleanup()
    {
        _exiting = true;
        _uiTimer.Stop();
        _s.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _hotkeys.Dispose();
        _engine.Dispose();
        Application.Current.Shutdown();
    }
}
