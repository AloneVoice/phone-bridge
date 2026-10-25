using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhoneBridge.App;

public partial class Theme : ResourceDictionary
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public Theme()
    {
        InitializeComponent();
    }

    /// <summary>Zaokrąglone rogi i cień Windows 11 dla okien bez systemowej belki.</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var preference = DwmwcpRound;
            // na Windows 10 atrybut nie istnieje – wywołanie zwraca błąd, który ignorujemy
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        Window.GetWindow((DependencyObject)sender)?.Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
