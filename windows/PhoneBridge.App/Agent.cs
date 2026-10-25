using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PhoneBridge.App.Windows;
using PhoneBridge.Core;
using PhoneBridge.Core.Config;
using PhoneBridge.Core.Files;
using PhoneBridge.Core.Network;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Server;
using Forms = System.Windows.Forms;

namespace PhoneBridge.App;

/// <summary>
/// Proces w tle: serwer dla telefonu, odpowiedzi discovery, IPC z menu kontekstowego, ikona w zasobniku
/// oraz okna dialogowe (implementacja <see cref="IBridgeUi"/>).
/// </summary>
internal sealed class Agent : IBridgeUi, IDisposable
{
    private static readonly TimeSpan SendBatchDelay = TimeSpan.FromMilliseconds(700);

    private readonly Application _app;
    private readonly string _exe;
    private readonly bool _startedInBackground;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _pendingPaths = [];
    private readonly DispatcherTimer _sendTimer;
    private ConfigStore? _config;
    private BridgeServer? _server;
    private DiscoveryResponder? _discovery;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;

    public Agent(Application app, string exe, bool startedInBackground)
    {
        _app = app;
        _exe = exe;
        _startedInBackground = startedInBackground;
        _sendTimer = new DispatcherTimer { Interval = SendBatchDelay };
        _sendTimer.Tick += (_, _) => FlushPendingSend();
    }

    private Dispatcher Dispatcher => _app.Dispatcher;
    private ConfigStore Config => _config!;
    private BridgeServer Server => _server!;

    public string ServerName { get; } = Environment.MachineName;

    public string StatusText
    {
        get
        {
            if (_server is null)
                return "Uruchamianie…";
            var connected = Server.Hub.ConnectedDevices;
            if (connected.Count > 0)
                return "Połączono: " + string.Join(", ", connected.Select(d => d.Name));
            return Config.Devices.Count == 0 ? "Brak sparowanego telefonu" : "Telefon niepołączony";
        }
    }

    public string HowToPair =>
        $"Aby sparować telefon: otwórz PhoneBridge na telefonie (ta sama sieć Wi-Fi) i wybierz komputer „{ServerName}”. " +
        "Następnie potwierdź ten sam 6-cyfrowy kod na obu urządzeniach.";

    public IReadOnlyList<TrustedDevice> PairedDevices => Config.Devices;
    public bool AutoAccept => Config.AutoAccept;
    public bool HotkeyEnabled => Config.HotkeyEnabled;
    public HotkeyCombo Hotkey => new(Config.Hotkey.Modifiers, Config.Hotkey.Key);
    public string ReceiveFolder => Config.DefaultFolder ?? DownloadsFolder;

    private static string DownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public async Task StartAsync()
    {
        Directory.CreateDirectory(ConfigStore.DefaultDirectory);
        Log.FilePath = Path.Combine(ConfigStore.DefaultDirectory, "log.txt");
        var configPath = Path.Combine(ConfigStore.DefaultDirectory, "config.json");
        var firstRun = !File.Exists(configPath);
        _config = new ConfigStore(configPath, new DpapiProtector());
        Log.Info($"Start PhoneBridge {typeof(Agent).Assembly.GetName().Version}");

        ShellIntegration.Register(_exe);
        if (firstRun)
            ShellIntegration.SetAutostart(_exe, true);

        EnsureFirewall();

        var certificate = Config.GetOrCreateServerCertificate("PhoneBridge " + ServerName);
        _server = new BridgeServer(Config, certificate, this, ServerName, new IPEndPoint(IPAddress.Any, Config.Port));
        try
        {
            await Server.StartAsync();
        }
        catch (IOException e)
        {
            Log.Error("Nie można uruchomić serwera", e);
            MessageBox.Show($"Port {Config.Port} jest zajęty przez inny program. PhoneBridge zostanie zamknięty.",
                "PhoneBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Exit();
            return;
        }

        _discovery = new DiscoveryResponder(ProtocolInfo.DiscoveryPort,
            () => new DiscoveryReply(ProtocolInfo.Version, ServerName, Server.Port, Server.ServerFingerprint));
        try { _discovery.Start(); }
        catch (SocketException e) { Log.Error("Wykrywanie w sieci niedostępne", e); }

        _tray = new TrayIcon(this);
        Server.Hub.ConnectionsChanged += () => Dispatcher.BeginInvoke(OnConnectionsChanged);
        Config.DevicesChanged += () => Dispatcher.BeginInvoke(() => _tray?.Refresh());
        _tray.Refresh();
        _ = Ipc.ServeAsync(message => Dispatcher.BeginInvoke(() => OnIpc(message)), _cts.Token);

        _hotkey = new GlobalHotkey(OnHotkey);
        if (Config.HotkeyEnabled && !_hotkey.Register(Hotkey))
            NotifyHotkeyTaken(Hotkey);

        if (NetworkSetup.IsConnectedToPublicNetwork())
            _tray.Notify("Sieć oznaczona jako publiczna",
                "Telefon nie połączy się z komputerem w sieci publicznej. Zmień profil sieci na „Prywatna” w Ustawieniach → Sieć i Internet.",
                Forms.ToolTipIcon.Warning);
        else if (Config.Devices.Count == 0 || !_startedInBackground)
            _tray.Notify("PhoneBridge działa", HowToPair);
    }

    private void EnsureFirewall()
    {
        if (NetworkSetup.FirewallRuleExists(_exe))
            return;
        var answer = MessageBox.Show(
            "Aby telefon mógł połączyć się z komputerem, PhoneBridge doda regułę Zapory Windows " +
            "(tylko sieci prywatne, tylko urządzenia z sieci lokalnej).\n\nWindows poprosi o zgodę administratora. Kontynuować?",
            "PhoneBridge", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes && !NetworkSetup.AddFirewallRules(_exe, Config.Port, ProtocolInfo.DiscoveryPort))
            Log.Error("Nie dodano reguły zapory");
    }

    private void OnConnectionsChanged()
    {
        _tray?.Refresh();
    }

    private void OnIpc(IpcMessage message)
    {
        switch (message.Command)
        {
            case IpcMessage.Send:
                // Explorer uruchamia osobny proces na każdy zaznaczony plik – zbieramy je w jedną ofertę
                _pendingPaths.AddRange(message.Paths);
                _sendTimer.Stop();
                _sendTimer.Start();
                break;
            case IpcMessage.Show:
                _tray?.ShowStatus();
                break;
        }
    }

    private void FlushPendingSend()
    {
        _sendTimer.Stop();
        var paths = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingPaths.Clear();
        _ = SendAsync(paths);
    }

    /// <summary>Ctrl+Alt+P: zaznaczone pliki z aktywnego Eksploratora, a poza nim okno wyboru plików.</summary>
    private void OnHotkey()
    {
        var selection = ExplorerSelection.GetForSelection();
        if (selection is { Count: > 0 })
            _ = SendAsync(selection);
        else
            PickAndSend();
    }

    public void ToggleHotkey()
    {
        var enable = !Config.HotkeyEnabled;
        if (enable && _hotkey?.Register(Hotkey) == false)
        {
            NotifyHotkeyTaken(Hotkey);
            return;
        }
        if (!enable)
            _hotkey?.Unregister();
        Config.Update(c => c.HotkeyEnabled = enable);
    }

    public void ChangeHotkey()
    {
        var previous = Hotkey;
        // obecny skrót nie może przechwytywać klawiszy, gdy użytkownik wciska nową kombinację
        _hotkey?.Unregister();
        var saved = false;
        var window = new HotkeyWindow(previous, combo =>
        {
            if (_hotkey?.Register(combo) != true)
                return false;
            saved = true;
            Config.Update(c =>
            {
                c.HotkeyModifiers = combo.Modifiers;
                c.HotkeyKey = combo.Key;
                c.HotkeyEnabled = true;
            });
            _tray?.Notify("Nowy skrót", $"Zaznacz pliki w Eksploratorze i naciśnij {combo}.");
            return true;
        });
        window.Closed += (_, _) =>
        {
            if (!saved && Config.HotkeyEnabled)
                _hotkey?.Register(previous);
        };
        window.Show();
    }

    private void NotifyHotkeyTaken(HotkeyCombo combo) =>
        _tray?.Notify("Skrót niedostępny", $"Skrót {combo} jest używany przez inny program. Zmień go w menu PhoneBridge.", Forms.ToolTipIcon.Warning);

    public void PickAndSend()
    {
        var dialog = new OpenFileDialog { Title = "Wybierz pliki do wysłania na telefon", Multiselect = true };
        if (dialog.ShowDialog() == true)
            _ = SendAsync(dialog.FileNames);
    }

    private async Task SendAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var files = await Task.Run(() => LocalFileCollector.Collect(paths));
        if (files.Count == 0)
        {
            _tray?.Notify("PhoneBridge", "Nie znaleziono plików do wysłania.", Forms.ToolTipIcon.Warning);
            return;
        }

        var device = Server.Hub.ConnectedDevices.FirstOrDefault() ?? Config.Devices.MaxBy(d => d.PairedAt);
        if (device is null)
        {
            _tray?.Notify("Brak sparowanego telefonu", HowToPair, Forms.ToolTipIcon.Warning);
            return;
        }

        var summary = files.Count == 1 ? $"{files[0].RelativeName} ({Format.Bytes(files[0].Size)})"
            : $"{Format.Files(files.Count)}, {Format.Bytes(files.Sum(f => f.Size))}";
        var window = new TransferWindow($"Wysyłanie na „{device.Name}”", summary);
        var transfer = Server.Hub.CreateOffer(device, files, window);
        window.CancelRequested += () => Server.Cancel(transfer);
        window.Show();
    }

    // ---------- IBridgeUi ----------

    public async Task<IncomingDecision> AskIncomingAsync(IncomingRequest request, CancellationToken cancellationToken)
    {
        if (Config.AutoAccept && Config.DefaultFolder is { } auto && Directory.Exists(auto))
            return new IncomingDecision(true, auto);

        var window = await Dispatcher.InvokeAsync(() =>
        {
            var w = new IncomingWindow(request, ReceiveFolder);
            w.Show();
            return w;
        });
        await using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(window.Close));

        var decision = await window.Result;
        if (decision.Accepted)
        {
            var remember = await Dispatcher.InvokeAsync(() => window.RememberAsAutoAccept);
            Config.Update(c =>
            {
                c.DefaultFolder = decision.Folder;
                c.AutoAccept = remember || c.AutoAccept;
            });
        }
        return decision;
    }

    public async Task<bool> AskPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        var window = await Dispatcher.InvokeAsync(() =>
        {
            var w = new PairingWindow(request);
            w.Show();
            return w;
        });
        await using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(window.Close));
        return await window.Result;
    }

    public ITransferObserver CreateIncomingObserver(IncomingTransfer transfer) =>
        Dispatcher.Invoke(() =>
        {
            var summary = transfer.Files.Count == 1 ? $"{transfer.Files[0].SafeName} ({Format.Bytes(transfer.TotalBytes)})"
                : $"{Format.Files(transfer.Files.Count)}, {Format.Bytes(transfer.TotalBytes)}";
            var window = new TransferWindow($"Odbieranie z „{transfer.Device.Name}”", summary, transfer.Folder);
            window.CancelRequested += () => Server.Cancel(transfer);
            window.Show();
            return (ITransferObserver)window;
        });

    public void OnPaired(TrustedDevice device) =>
        Dispatcher.BeginInvoke(() => _tray?.Notify("Sparowano", $"Telefon „{device.Name}” może teraz wysyłać i odbierać pliki."));

    // ---------- akcje z menu ----------

    public void Forget(TrustedDevice device)
    {
        var answer = MessageBox.Show($"Usunąć parowanie z „{device.Name}”? Telefon straci dostęp do komputera.",
            "PhoneBridge", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        Config.RemoveDevice(device.Fingerprint);
        Server.Hub.Disconnect(device.Fingerprint);
    }

    public void ToggleAutoAccept()
    {
        if (Config.AutoAccept)
        {
            Config.Update(c => c.AutoAccept = false);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Folder, do którego zapisywać pliki bez pytania", InitialDirectory = ReceiveFolder };
        if (dialog.ShowDialog() == true)
            Config.Update(c =>
            {
                c.DefaultFolder = dialog.FolderName;
                c.AutoAccept = true;
            });
    }

    public void ToggleAutostart() => ShellIntegration.SetAutostart(_exe, !ShellIntegration.IsAutostartEnabled());

    public void Exit()
    {
        _app.Shutdown();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _discovery?.Dispose();
        if (_server is not null)
            Task.Run(() => _server.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }
}
