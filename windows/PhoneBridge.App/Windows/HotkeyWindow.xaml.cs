using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhoneBridge.App.Windows;

/// <summary>Przechwytuje kombinację klawiszy. Zapis sprawdza, czy skrót nie jest zajęty.</summary>
public partial class HotkeyWindow : Window
{
    private readonly Func<HotkeyCombo, bool> _tryApply;
    private HotkeyCombo _combo;

    internal HotkeyWindow(HotkeyCombo current, Func<HotkeyCombo, bool> tryApply)
    {
        InitializeComponent();
        Theme.ApplyRoundedCorners(this);
        _tryApply = tryApply;
        Display(current, "Obecny skrót.");
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => Activate();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Close();
            return;
        }

        var combo = HotkeyCombo.From(Keyboard.Modifiers, key);
        Display(combo, combo.IsAllowed ? "Kliknij „Zapisz”, aby użyć tego skrótu."
            : "Dodaj Ctrl, Alt lub Win – sam klawisz (poza F1–F24) przeszkadzałby w pisaniu.");
    }

    private void Display(HotkeyCombo combo, string message)
    {
        _combo = combo;
        KeyTiles.Children.Clear();
        foreach (var part in combo.Parts)
        {
            if (KeyTiles.Children.Count > 0)
                KeyTiles.Children.Add(new TextBlock { Text = "+", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("PbMuted"), FontSize = 18 });
            KeyTiles.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("PbCoralSoft"),
                Padding = new Thickness(14, 8, 14, 8),
                Child = new TextBlock
                {
                    Text = part,
                    FontFamily = (FontFamily)FindResource("PbFontDisplay"),
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("PbOnCoralSoft"),
                },
            });
        }
        Message.Text = message;
        Message.Foreground = (Brush)FindResource(combo.IsAllowed ? "PbMuted" : "PbError");
        SaveButton.IsEnabled = combo.IsAllowed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_tryApply(_combo))
        {
            Close();
            return;
        }
        Message.Text = $"Skrót {_combo} jest już używany przez inny program. Wybierz inną kombinację.";
        Message.Foreground = (Brush)FindResource("PbError");
    }

    private void OnReset(object sender, RoutedEventArgs e) => Display(HotkeyCombo.Default, "Kliknij „Zapisz”, aby przywrócić domyślny skrót.");

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
