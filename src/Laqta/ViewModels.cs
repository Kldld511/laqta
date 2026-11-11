using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Laqta.Audio;

namespace Laqta;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record Option(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row in the "audio tracks" card.</summary>
public sealed class TrackVM : Notifier
{
    private readonly AudioTrack _track;
    private readonly Action<bool> _changed; // true = pipeline restart needed
    private double _level;
    private string _source = "";
    private Option? _selected;

    public TrackVM(AudioTrack track, Action<bool> changed)
    {
        _track = track;
        _changed = changed;
        HasOptions = track.Kind is TrackKind.Game or TrackKind.Mic;
    }

    public AudioTrack Track => _track;
    public bool HasOptions { get; }
    public Visibility OptionsVisibility => HasOptions ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<Option> Options { get; } = new();

    public string Icon => _track.Kind switch
    {
        TrackKind.Game => "",
        TrackKind.Mic => "",
        TrackKind.Discord => "",
        _ => "",
    };

    public string Title => Loc.T(_track.Kind switch
    {
        TrackKind.Game => "S_TrackGame",
        TrackKind.Mic => "S_TrackMic",
        TrackKind.Discord => "S_TrackDiscord",
        _ => "S_TrackBrowser",
    });

    public bool Enabled
    {
        get => _track.Settings.Enabled;
        set { _track.Settings.Enabled = value; Raise(); _changed(true); }
    }

    public double Gain
    {
        get => _track.Settings.Gain;
        set { _track.Settings.Gain = Math.Round(value, 2); Raise(); Raise(nameof(GainText)); _changed(false); }
    }

    public string GainText => $"{_track.Settings.Gain * 100:0}%";

    public double Level { get => _level; set { if (Math.Abs(_level - value) > 0.002) { _level = value; Raise(); } } }

    public string Source { get => _source; set { if (_source != value) { _source = value; Raise(); } } }

    public Option? Selected
    {
        get => _selected;
        set
        {
            if (value == null || Equals(_selected, value)) return;
            _selected = value;
            Raise();
            if (_track.Settings.Target != value.Value)
            {
                _track.Settings.Target = value.Value;
                _changed(false);
            }
        }
    }

    /// <summary>Rebuilds the picker (running apps for the game track, input devices for the mic).</summary>
    public void RefreshOptions()
    {
        if (!HasOptions) return;
        var items = new List<Option>();
        if (_track.Kind == TrackKind.Game)
        {
            items.Add(new Option("", Loc.T("S_Auto")));
            foreach (var name in ProcessResolver.WindowedApps()) items.Add(new Option(name, name));
        }
        else
        {
            items.Add(new Option("", Loc.T("S_DefaultMic")));
            foreach (var d in CoreAudio.ListMicrophones()) items.Add(new Option(d.Id, d.Name));
        }
        var target = _track.Settings.Target ?? "";
        if (items.All(i => !i.Value.Equals(target, StringComparison.OrdinalIgnoreCase))) items.Add(new Option(target, target));

        _selected = null;
        Options.Clear();
        foreach (var i in items) Options.Add(i);
        _selected = Options.First(i => i.Value.Equals(target, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(Selected));
    }

    public void RefreshTexts()
    {
        Raise(nameof(Title));
        RefreshOptions();
    }
}

/// <summary>One saved clip or screenshot in the gallery.</summary>
public sealed class MediaItem : Notifier
{
    private ImageSource? _thumb;
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Info { get; init; }
    public required bool IsVideo { get; init; }
    public string Glyph => IsVideo ? "" : "";
    public string Kind => Loc.T(IsVideo ? "S_Video" : "S_Image");
    public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(); } }
}
