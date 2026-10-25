using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PhoneBridge.Core.Server;

namespace PhoneBridge.App.Windows;

/// <summary>Małe okno postępu w prawym dolnym rogu ekranu. Implementuje obserwatora transferu.</summary>
public partial class TransferWindow : Window, ITransferObserver
{
    private static readonly List<TransferWindow> Open = [];
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(6);

    private readonly string? _folder;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastUiUpdate = -1000;
    private long _speedBytes;
    private long _speedTicks;
    private bool _finished;
    private DispatcherTimer? _closeTimer;

    public TransferWindow(string header, string summary, string? folder = null)
    {
        InitializeComponent();
        Theme.ApplyRoundedCorners(this);
        Header.Text = header;
        Summary.Text = summary;
        _folder = folder;
        // folder docelowy jest znany tylko przy odbieraniu
        if (folder is not null)
            DirectionGlyph.Data = (System.Windows.Media.Geometry)FindResource("PbGlyphDown");

        Loaded += (_, _) =>
        {
            Open.Add(this);
            Reposition();
        };
        Closed += (_, _) =>
        {
            Open.Remove(this);
            if (!_finished)
                CancelRequested?.Invoke();
            foreach (var w in Open)
                w.Reposition();
        };
        MouseEnter += (_, _) => _closeTimer?.Stop();
        MouseLeave += (_, _) => _closeTimer?.Start();
    }

    public event Action? CancelRequested;

    public void OnStatus(TransferStatus status, string? message) =>
        Dispatcher.BeginInvoke(() => ApplyStatus(status, message));

    public void OnProgress(long bytesDone, long bytesTotal)
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastUiUpdate) < 150 && bytesDone < bytesTotal)
            return;
        Interlocked.Exchange(ref _lastUiUpdate, now);
        Dispatcher.BeginInvoke(() => ApplyProgress(bytesDone, bytesTotal));
    }

    private void ApplyStatus(TransferStatus status, string? message)
    {
        Status.Text = message ?? status switch
        {
            TransferStatus.Queued => "W kolejce…",
            TransferStatus.WaitingForAccept => "Czekam na akceptację…",
            TransferStatus.InProgress => "Przesyłanie…",
            _ => "",
        };
        Progress.IsIndeterminate = status is TransferStatus.Queued or TransferStatus.WaitingForAccept;

        if (status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Cancelled)
        {
            _finished = true;
            Speed.Text = "";
            ActionButton.Content = "Zamknij";
            if (status == TransferStatus.Completed)
            {
                Progress.Value = Progress.Maximum;
                Progress.Foreground = (System.Windows.Media.Brush)FindResource("PbSuccess");
                OpenFolderButton.Visibility = _folder is null ? Visibility.Collapsed : Visibility.Visible;
                _closeTimer = new DispatcherTimer { Interval = AutoCloseDelay };
                _closeTimer.Tick += (_, _) => Close();
                if (!IsMouseOver)
                    _closeTimer.Start();
            }
            else
            {
                Progress.Value = 0;
                Status.Foreground = (System.Windows.Media.Brush)FindResource("PbErrorOnDark");
            }
        }
    }

    private void ApplyProgress(long done, long total)
    {
        if (_finished)
            return;
        Progress.IsIndeterminate = false;
        Progress.Value = total == 0 ? Progress.Maximum : (double)done / total * Progress.Maximum;
        Status.Text = $"{Format.Bytes(done)} z {Format.Bytes(total)}";

        // prędkość liczona z ostatnich ~2 s
        var ticks = _clock.ElapsedTicks;
        var elapsed = Stopwatch.GetElapsedTime(_speedTicks, ticks);
        if (_speedTicks == 0 || elapsed > TimeSpan.FromSeconds(2))
        {
            if (_speedTicks != 0 && done > _speedBytes)
            {
                var perSecond = (done - _speedBytes) / elapsed.TotalSeconds;
                var remaining = TimeSpan.FromSeconds((total - done) / perSecond);
                Speed.Text = $"{Format.Bytes((long)perSecond)}/s · {Format.Duration(remaining)}";
            }
            _speedTicks = ticks;
            _speedBytes = done;
        }
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        var offset = 0.0;
        foreach (var w in Open)
        {
            if (w == this)
                break;
            offset += w.ActualHeight + 8;
        }
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12 - offset;
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (!_finished)
        {
            _finished = true;
            CancelRequested?.Invoke();
        }
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_folder is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
        Close();
    }
}
