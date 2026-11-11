using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Laqta.Core;

/// <summary>System-wide hotkeys (work while a game has focus) via RegisterHotKey.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("LaqtaHotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public bool Register(int id, string gesture, Action action)
    {
        UnregisterHotKey(_source.Handle, id);
        _actions.Remove(id);
        if (!TryParse(gesture, out uint mods, out uint vk)) return false;
        if (!RegisterHotKey(_source.Handle, id, mods | 0x4000 /* MOD_NOREPEAT */, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": mods |= 1; break;
                case "ctrl": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }
}
