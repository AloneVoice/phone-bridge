using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PhoneBridge.Core.Config;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Security;
using PhoneBridge.Core.Server;

namespace PhoneBridge.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("pb-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

internal sealed class PlainProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

internal sealed class FakeUi : IBridgeUi
{
    public Func<IncomingRequest, CancellationToken, Task<IncomingDecision>> OnIncoming { get; set; } =
        (_, _) => Task.FromResult(IncomingDecision.Rejected);

    public Func<PairingRequest, Task<bool>> OnPairing { get; set; } = _ => Task.FromResult(true);

    public ConcurrentQueue<PairingRequest> PairingRequests { get; } = new();
    public ConcurrentQueue<TrustedDevice> Paired { get; } = new();
    public RecordingObserver IncomingObserver { get; } = new();

    public Task<IncomingDecision> AskIncomingAsync(IncomingRequest request, CancellationToken cancellationToken) =>
        OnIncoming(request, cancellationToken);

    public Task<bool> AskPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        PairingRequests.Enqueue(request);
        return OnPairing(request);
    }

    public ITransferObserver CreateIncomingObserver(IncomingTransfer transfer) => IncomingObserver;

    public void OnPaired(TrustedDevice device) => Paired.Enqueue(device);
}

internal sealed class RecordingObserver : ITransferObserver
{
    private readonly TaskCompletionSource<TransferStatus> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<TransferStatus> Statuses { get; } = new();
    public long LastProgress { get; private set; }

    public Task<TransferStatus> Finished => _finished.Task;

    public void OnStatus(TransferStatus status, string? message)
    {
        Statuses.Enqueue(status);
        if (status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Cancelled)
            _finished.TrySetResult(status);
    }

    public void OnProgress(long bytesDone, long bytesTotal) => LastProgress = bytesDone;
}

/// <summary>Serwer na losowym porcie loopback z tymczasową konfiguracją.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly TempDir _dir;

    private TestServer(TempDir dir, BridgeServer server, ConfigStore config, FakeUi ui)
    {
        _dir = dir;
        Server = server;
        Config = config;
        Ui = ui;
    }

    public BridgeServer Server { get; }
    public ConfigStore Config { get; }
    public FakeUi Ui { get; }
    public Uri BaseUri => new($"https://127.0.0.1:{Server.Port}");
    public string DownloadFolder => Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "odebrane")).FullName;

    public static async Task<TestServer> StartAsync()
    {
        var dir = new TempDir();
        var config = new ConfigStore(System.IO.Path.Combine(dir.Path, "config.json"), new PlainProtector());
        var cert = config.GetOrCreateServerCertificate("TEST-PC");
        var ui = new FakeUi();
        var server = new BridgeServer(config, cert, ui, "TEST-PC", new IPEndPoint(IPAddress.Loopback, 0));
        await server.StartAsync();
        return new TestServer(dir, server, config, ui);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        _dir.Dispose();
    }
}

/// <summary>Udaje telefon: własny certyfikat klienta i przypięty odcisk serwera.</summary>
internal sealed class TestPhone : IDisposable
{
    private readonly SocketsHttpHandler _handler;

    public TestPhone(TestServer server, string? pinnedFingerprint = null)
    {
        Certificate = CertificateFactory.Load(CertificateFactory.CreatePfx("Test Phone"));
        Fingerprint = PhoneBridge.Core.Security.Fingerprint.Of(Certificate);
        var pin = pinnedFingerprint ?? server.Server.ServerFingerprint;
        _handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { Certificate },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData())) == pin,
            },
        };
        Http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = server.BaseUri };
        BaseUri = server.BaseUri;
    }

    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public HttpClient Http { get; }
    public Uri BaseUri { get; }

    public TrustedDevice PairDirectly(TestServer server) => server.Config.AddDevice(Fingerprint, "Testowy telefon");

    public Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body) => Http.PostAsJsonAsync(path, body, Json.Options);

    public async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync()
    {
        var ws = new ClientWebSocket();
        var uri = new UriBuilder(BaseUri) { Scheme = "wss", Path = "/v1/ws" }.Uri;
        await ws.ConnectAsync(uri, new HttpMessageInvoker(_handler, disposeHandler: false), CancellationToken.None);
        return ws;
    }

    public static async Task<WsMessage> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ws.ReceiveAsync(buffer, timeout.Token);
        return JsonSerializer.Deserialize<WsMessage>(Encoding.UTF8.GetString(buffer, 0, result.Count), Json.Options)!;
    }

    public static Task SendAsync(ClientWebSocket ws, WsMessage message) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json.Options), WebSocketMessageType.Text, true, CancellationToken.None);

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
        Certificate.Dispose();
    }
}
