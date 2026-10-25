using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhoneBridge.Core.Server;

namespace PhoneBridge.App.Windows;

public partial class PairingWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PairingWindow(PairingRequest request)
    {
        InitializeComponent();
        Theme.ApplyRoundedCorners(this);
        Header.Text = $"„{request.DeviceName}” chce się sparować";
        Address.Text = $"Telefon w sieci lokalnej · {request.RemoteAddress}";
        FillCodeTiles(request.Code);

        Closed += (_, _) => _result.TrySetResult(false);
        Loaded += (_, _) =>
        {
            Activate();
            SystemSounds.Asterisk.Play();
        };
    }

    public Task<bool> Result => _result.Task;

    private void FillCodeTiles(string code)
    {
        for (var i = 0; i < code.Length; i++)
        {
            CodeTiles.Children.Add(new Border
            {
                Width = 46,
                Height = 60,
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("PbCoralSoft"),
                Margin = new Thickness(i == 3 ? 16 : i > 0 ? 6 : 0, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = code[i].ToString(),
                    FontFamily = (FontFamily)FindResource("PbFontDisplay"),
                    FontSize = 30,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("PbOnCoralSoft"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnReject(object sender, RoutedEventArgs e) => Close();
}
