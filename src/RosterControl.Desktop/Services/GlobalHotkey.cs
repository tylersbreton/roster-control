using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace RosterControl.Desktop.Services;

/// <summary>
/// A system-wide keyboard shortcut (Windows <c>RegisterHotKey</c>) delivered to a window's message loop, so it works
/// while another app (or the game, in borderless mode) has focus. Only one shortcut at a time.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x5243; // "RC"
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly TopLevel _window;
    private readonly Action _pressed;
    private IntPtr _hwnd;
    private bool _hooked;

    public GlobalHotkey(TopLevel window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
    }

    public bool IsRegistered { get; private set; }

    /// <summary>Registers <paramref name="gesture"/> (replacing any previous one). False when unsupported or taken by another app.</summary>
    public bool Register(KeyGesture gesture)
    {
        Unregister();
        if (!OperatingSystem.IsWindows() || !IsValid(gesture)) return false;
        if (_window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return false;
        _hwnd = handle.Handle;
        if (!_hooked)
        {
            Win32Properties.AddWndProcHookCallback(_window, Hook);
            _hooked = true;
        }
        IsRegistered = RegisterHotKey(_hwnd, HotkeyId, Modifiers(gesture.KeyModifiers) | ModNoRepeat, VirtualKey(gesture.Key));
        return IsRegistered;
    }

    public void Unregister()
    {
        if (IsRegistered && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        IsRegistered = false;
    }

    public void Dispose()
    {
        Unregister();
        if (_hooked) Win32Properties.RemoveWndProcHookCallback(_window, Hook);
        _hooked = false;
    }

    private IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            Dispatcher.UIThread.Post(_pressed);
        }
        return IntPtr.Zero;
    }

    /// <summary>Needs Ctrl, Alt or Win plus a letter, digit, F-key, Space or another supported key.</summary>
    public static bool IsValid(KeyGesture gesture) =>
        VirtualKey(gesture.Key) != 0 && (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            gesture = KeyGesture.Parse(text);
            return IsValid(gesture);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>"Ctrl+Shift+Space" (as stored and shown).</summary>
    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(gesture.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(gesture.Key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Key.OemTilde => "`",
            _ => gesture.Key.ToString(),
        });
        return string.Join("+", parts);
    }

    internal static uint Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Alt) ? ModAlt : 0) |
        (modifiers.HasFlag(KeyModifiers.Control) ? ModControl : 0) |
        (modifiers.HasFlag(KeyModifiers.Shift) ? ModShift : 0) |
        (modifiers.HasFlag(KeyModifiers.Meta) ? ModWin : 0);

    /// <summary>Windows virtual-key code; 0 when the key isn't supported as a shortcut.</summary>
    internal static uint VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41u + (uint)(key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(key - Key.D0),
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(key - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60u + (uint)(key - Key.NumPad0),
        Key.Space => 0x20,
        Key.OemTilde => 0xC0,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        _ => 0,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
