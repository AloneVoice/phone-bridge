using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Network;

public static class NetworkUtil
{
    /// <summary>Czy adres należy do sieci lokalnej (prywatne IPv4, link-local, IPv6 ULA, loopback).</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal);
    }
}

/// <summary>Odpowiada na broadcasty UDP telefonów szukających komputera.</summary>
public sealed class DiscoveryResponder : IDisposable
{
    private const int SioUdpConnReset = -1744830452;

    private readonly int _port;
    private readonly Func<DiscoveryReply> _reply;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryResponder(int port, Func<DiscoveryReply> reply)
    {
        _port = port;
        _reply = reply;
    }

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        if (OperatingSystem.IsWindows())
            _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); // ICMP "port unreachable" nie ma zrywać pętli
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var udp = _udp!;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(_cts.Token);
                if (received.Buffer.Length > 512 || !NetworkUtil.IsLocal(received.RemoteEndPoint.Address) || !IsDiscoverRequest(received.Buffer))
                    continue;
                var payload = JsonSerializer.SerializeToUtf8Bytes(_reply(), Json.Options);
                await udp.SendAsync(payload, received.RemoteEndPoint, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                Log.Error("Błąd discovery", e);
            }
        }
    }

    private static bool IsDiscoverRequest(byte[] buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("pb", out var pb) && pb.ValueKind == JsonValueKind.Number && pb.GetInt32() == ProtocolInfo.Version
                && root.TryGetProperty("q", out var q) && q.ValueKind == JsonValueKind.String && q.GetString() == "discover";
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
