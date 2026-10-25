using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using PhoneBridge.Core;

namespace PhoneBridge.App;

/// <summary>Kombinacja klawiszy w formacie RegisterHotKey.</summary>
internal readonly record struct HotkeyCombo(uint Modifiers, uint Key)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    public static readonly HotkeyCombo Default = new(Control | Alt, 0x50);

    public static HotkeyCombo From(ModifierKeys modifiers, Key key)
    {
        uint m = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) m |= Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) m |= Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) m |= Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) m |= Win;
        return new HotkeyCombo(m, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private Key WpfKey => KeyInterop.KeyFromVirtualKey((int)Key);

    public bool IsFunctionKey => WpfKey is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24;

    /// <summary>Bez modyfikatora tylko klawisze funkcyjne – inaczej skrót przechwyciłby zwykłe pisanie.</summary>
    public bool IsAllowed => Key != 0 && (Modifiers != 0 || IsFunctionKey) && Modifiers != Shift;

    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Win) != 0) parts.Add("Win");
            parts.Add(KeyName(WpfKey));
            return parts;
        }
    }

    public override string ToString() => string.Join("+", Parts);

    private static string KeyName(Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
        >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num" + (int)(key - System.Windows.Input.Key.NumPad0),
        System.Windows.Input.Key.OemPlus => "=",
        System.Windows.Input.Key.OemMinus => "-",
        System.Windows.Input.Key.OemComma => ",",
        System.Windows.Input.Key.OemPeriod => ".",
        System.Windows.Input.Key.Space => "Spacja",
        System.Windows.Input.Key.Insert => "Insert",
        System.Windows.Input.Key.Home => "Home",
        System.Windows.Input.Key.End => "End",
        System.Windows.Input.Key.PageUp => "PgUp",
        System.Windows.Input.Key.PageDown => "PgDn",
        _ => key.ToString(),
    };
}

/// <summary>Globalny skrót klawiaturowy (RegisterHotKey) obsługiwany przez niewidoczne okno komunikatów.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int Id = 0x5042;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private HotkeyCombo? _registered;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("PhoneBridge.Hotkey") { ParentWindow = HwndMessage });
        _source.AddHook(WndProc);
    }

    /// <summary>Rejestruje skrót (zastępując poprzedni). Zwraca false, gdy kombinacja jest zajęta przez inny program.</summary>
    public bool Register(HotkeyCombo combo)
    {
        Unregister();
        if (!RegisterHotKey(_source.Handle, Id, combo.Modifiers | ModNoRepeat, combo.Key))
        {
            Log.Error($"Skrót {combo} jest zajęty przez inny program");
            return false;
        }
        _registered = combo;
        return true;
    }

    public void Unregister()
    {
        if (_registered is null)
            return;
        UnregisterHotKey(_source.Handle, Id);
        _registered = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Pliki zaznaczone w aktywnym oknie (i aktywnej karcie) Eksploratora.</summary>
internal static class ExplorerSelection
{
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr hwnd); // pierwsza metoda IOleWindow – tylko ta jest potrzebna
    }

    /// <summary>Zwraca null, gdy na pierwszym planie nie ma okna Eksploratora.</summary>
    public static IReadOnlyList<string>? GetForSelection()
    {
        var foreground = GetForegroundWindow();
        // Windows 11: wiele kart w jednym oknie; pierwsza ShellTabWindowClass to karta aktywna
        var activeTab = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                if (new IntPtr((long)window.HWND) != foreground)
                    continue;
                if (activeTab != IntPtr.Zero && TabWindow(window) is { } tab && tab != activeTab)
                    continue;

                var paths = new List<string>();
                foreach (dynamic item in window.Document.SelectedItems())
                    paths.Add((string)item.Path);
                return paths;
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Error("Nie można odczytać zaznaczenia w Eksploratorze", e);
        }
        return null;
    }

    private static IntPtr? TabWindow(object window)
    {
        if (window is not IServiceProvider provider)
            return null;
        var service = SidTopLevelBrowser;
        var iid = IidShellBrowser;
        if (provider.QueryService(ref service, ref iid, out var pointer) != 0 || pointer == IntPtr.Zero)
            return null;
        try
        {
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pointer);
            return browser.GetWindow(out var hwnd) == 0 ? hwnd : null;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
}
