using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhoneBridge.Core.Config;
using PhoneBridge.Core.Files;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Server;

/// <summary>
/// Połączenia WebSocket z telefonami oraz oferty komputer → telefon.
/// </summary>
public sealed class TransferHub
{
    private const int MaxMessageBytes = 64 * 1024;

    private sealed class Connection(TrustedDevice device, WebSocket socket)
    {
        public TrustedDevice Device { get; } = device;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, OutgoingTransfer> _outgoing = new();
    private readonly string _serverName;

    public TransferHub(string serverName)
    {
        _serverName = serverName;
    }

    public event Action? ConnectionsChanged;

    /// <summary>Połączone telefony, ostatnio połączony pierwszy.</summary>
    public IReadOnlyList<TrustedDevice> ConnectedDevices =>
        _connections.Values.OrderByDescending(c => c.ConnectedAt).Select(c => c.Device).ToList();

    public bool IsConnected(string fingerprint) => _connections.ContainsKey(fingerprint);

    public OutgoingTransfer CreateOffer(TrustedDevice device, IReadOnlyList<LocalFile> files, ITransferObserver observer)
    {
        var transferFiles = files.Select(f => new TransferFile { Name = f.RelativeName, Size = f.Size, LocalPath = f.FullPath }).ToList();
        var transfer = new OutgoingTransfer(device, transferFiles) { Observer = observer };
        _outgoing[transfer.Id] = transfer;
        transfer.Finished += t => _outgoing.TryRemove(t.Id, out _);
        transfer.SetStatus(TransferStatus.Queued, IsConnected(device.Fingerprint) ? null : "Czekam na połączenie z telefonem…");
        _ = SendOfferAsync(transfer);
        return transfer;
    }

    internal OutgoingTransfer? FindOutgoing(string id, TrustedDevice device) =>
        _outgoing.TryGetValue(id, out var t) && t.Device.Fingerprint == device.Fingerprint ? t : null;

    internal IEnumerable<Transfer> ActiveOutgoing => _outgoing.Values;

    public void Cancel(Transfer transfer)
    {
        if (transfer.SetStatus(TransferStatus.Cancelled, "Anulowano"))
            _ = SendAsync(transfer.Device.Fingerprint, new WsMessage(WsTypes.Cancel, transfer.Id));
    }

    public void Disconnect(string fingerprint)
    {
        if (_connections.TryRemove(fingerprint, out var connection))
        {
            connection.Socket.Abort();
            ConnectionsChanged?.Invoke();
        }
    }

    public Task<bool> SendAsync(string fingerprint, WsMessage message) =>
        _connections.TryGetValue(fingerprint, out var c) ? SendAsync(c, message) : Task.FromResult(false);

    private async Task SendOfferAsync(OutgoingTransfer transfer)
    {
        if (transfer.Status is not (TransferStatus.Queued or TransferStatus.WaitingForAccept))
            return;
        var sent = await SendAsync(transfer.Device.Fingerprint, new WsMessage(WsTypes.Offer, transfer.Id, transfer.ToEntries()));
        if (sent && transfer.Status == TransferStatus.Queued)
            transfer.SetStatus(TransferStatus.WaitingForAccept, "Czekam na akceptację na telefonie…");
    }

    private static async Task<bool> SendAsync(Connection connection, WsMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        await connection.SendLock.WaitAsync();
        try
        {
            await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            connection.SendLock.Release();
        }
    }

    internal async Task HandleAsync(WebSocket socket, TrustedDevice device, CancellationToken cancellationToken)
    {
        var connection = new Connection(device, socket);
        if (_connections.TryGetValue(device.Fingerprint, out var previous))
            previous.Socket.Abort();
        _connections[device.Fingerprint] = connection;
        ConnectionsChanged?.Invoke();
        Log.Info("Telefon połączony");

        try
        {
            await SendAsync(connection, new WsMessage(WsTypes.Hello, ServerName: _serverName, Protocol: ProtocolInfo.Version));
            foreach (var transfer in _outgoing.Values.Where(t => t.Device.Fingerprint == device.Fingerprint))
                await SendOfferAsync(transfer);
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // rozłączenie telefonu
        }
        finally
        {
            if (_connections.TryRemove(new KeyValuePair<string, Connection>(device.Fingerprint, connection)))
                ConnectionsChanged?.Invoke();
            Log.Info("Telefon rozłączony");
        }
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (connection.Socket.State == WebSocketState.Open)
        {
            var result = await connection.Socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage)
                continue;

            if (result.MessageType == WebSocketMessageType.Text)
                HandleMessage(connection, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void HandleMessage(Connection connection, string text)
    {
        WsMessage? message;
        try { message = JsonSerializer.Deserialize<WsMessage>(text, Json.Options); }
        catch (JsonException) { return; }
        if (message?.Id is not { } id || FindOutgoing(id, connection.Device) is not { } transfer)
            return;

        switch (message.Type)
        {
            case WsTypes.Accept when transfer.Status is TransferStatus.Queued or TransferStatus.WaitingForAccept:
                transfer.SetStatus(TransferStatus.InProgress, "Telefon pobiera pliki…");
                break;
            case WsTypes.Reject:
                transfer.SetStatus(TransferStatus.Rejected, "Odrzucono na telefonie");
                break;
            case WsTypes.Cancel:
                transfer.Cancel("Anulowano na telefonie");
                break;
        }
    }
}
