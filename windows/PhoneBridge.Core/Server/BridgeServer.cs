using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using PhoneBridge.Core.Config;
using PhoneBridge.Core.Files;
using PhoneBridge.Core.Network;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Security;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace PhoneBridge.Core.Server;

/// <summary>
/// Serwer HTTPS + WebSocket na komputerze. Wzajemne TLS: każde żądanie niesie certyfikat telefonu.
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private const string FingerprintKey = "pb.fp";
    private const string DeviceKey = "pb.device";
    private const int BufferSize = 256 * 1024;

    private readonly ConfigStore _config;
    private readonly X509Certificate2 _certificate;
    private readonly IBridgeUi _ui;
    private readonly IPEndPoint _endpoint;
    private readonly PairingManager _pairing;
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incoming = new();
    private WebApplication? _app;
    private Timer? _sweepTimer;

    public BridgeServer(ConfigStore config, X509Certificate2 certificate, IBridgeUi ui, string serverName, IPEndPoint endpoint, PairingManager? pairing = null)
    {
        _config = config;
        _certificate = certificate;
        _ui = ui;
        _endpoint = endpoint;
        _pairing = pairing ?? new PairingManager();
        ServerName = serverName;
        ServerFingerprint = Fingerprint.Of(certificate);
        Hub = new TransferHub(serverName);
    }

    public string ServerName { get; }
    public string ServerFingerprint { get; }
    public TransferHub Hub { get; }
    public int Port { get; private set; }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(_endpoint, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = _certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    // certyfikaty są samopodpisane; zaufanie sprawdzamy po odcisku w Guard
                    ClientCertificateValidation = (_, _, _) => true,
                });
            });
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
        app.Use(Guard);

        // rzutowanie na Delegate: inaczej metoda (HttpContext) => Task<IResult> zostałaby RequestDelegate, a wynik by przepadł
        app.MapPost("/v1/pair/start", (Delegate)PairStart);
        app.MapPost("/v1/pair/reveal", (Delegate)PairReveal);
        app.MapPost("/v1/pair/confirm", (Delegate)PairConfirm);
        app.Map("/v1/ws", WebSocketEndpoint);
        app.MapPost("/v1/incoming", (Delegate)IncomingOffer);
        app.MapPut("/v1/incoming/{id}/{index:int}", IncomingContent);
        app.MapPost("/v1/incoming/{id}/{index:int}/commit", IncomingCommit);
        app.MapPost("/v1/incoming/{id}/cancel", IncomingCancel);
        app.MapGet("/v1/outgoing/{id}/{index:int}", OutgoingContent);
        app.MapPost("/v1/outgoing/{id}/{index:int}/done", OutgoingDone);

        await app.StartAsync();
        _app = app;
        Port = new Uri(app.Urls.First()).Port;
        _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Log.Info($"Serwer nasłuchuje na porcie {Port}");
    }

    /// <summary>Anuluje transfer w dowolnym kierunku i powiadamia telefon.</summary>
    public void Cancel(Transfer transfer) => Hub.Cancel(transfer);

    public async ValueTask DisposeAsync()
    {
        if (_sweepTimer is not null)
            await _sweepTimer.DisposeAsync();
        foreach (var t in _incoming.Values)
            t.Cancel("Zamknięto aplikację");
        foreach (var t in Hub.ActiveOutgoing.ToList())
            t.Cancel("Zamknięto aplikację");
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private async Task Guard(HttpContext ctx, Func<Task> next)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        var cert = ctx.Connection.ClientCertificate;
        if (remote is null || !NetworkUtil.IsLocal(remote) || cert is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var fingerprint = Fingerprint.Of(cert);
        ctx.Items[FingerprintKey] = fingerprint;
        if (!ctx.Request.Path.StartsWithSegments("/v1/pair"))
        {
            var device = _config.FindDevice(fingerprint);
            if (device is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            ctx.Items[DeviceKey] = device;
        }
        await next();
    }

    private static string FingerprintOf(HttpContext ctx) => (string)ctx.Items[FingerprintKey]!;
    private static TrustedDevice DeviceOf(HttpContext ctx) => (TrustedDevice)ctx.Items[DeviceKey]!;

    // ---------- parowanie ----------

    private async Task<IResult> PairStart(HttpContext ctx)
    {
        var req = await ReadJsonAsync<PairStartRequest>(ctx);
        if (req is null)
            return Results.BadRequest();

        var deviceName = SanitizeDeviceName(req.DeviceName);
        var (error, pairingId, nonce) = _pairing.Start(FingerprintOf(ctx), deviceName, req.Commitment);
        return error switch
        {
            PairStartError.None => Results.Json(new PairStartResponse(pairingId!, nonce!, ServerName), Json.Options),
            PairStartError.InvalidRequest => Results.BadRequest(),
            _ => Results.StatusCode(StatusCodes.Status429TooManyRequests),
        };
    }

    private async Task<IResult> PairReveal(HttpContext ctx)
    {
        var req = await ReadJsonAsync<PairRevealRequest>(ctx);
        var fingerprint = FingerprintOf(ctx);
        var revealed = req is null ? null : _pairing.Reveal(fingerprint, req.PairingId, req.Nonce, ServerFingerprint);
        if (revealed is not { } r)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        bool accepted;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
        {
            timeout.CancelAfter(PairingManager.Lifetime);
            try
            {
                accepted = await _ui.AskPairingAsync(new PairingRequest(r.DeviceName, r.Code, ctx.Connection.RemoteIpAddress!.ToString()), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                accepted = false;
            }
        }
        _pairing.SetDecision(fingerprint, req!.PairingId, accepted);
        return Results.Json(new PairRevealResponse(accepted), Json.Options);
    }

    private async Task<IResult> PairConfirm(HttpContext ctx)
    {
        var req = await ReadJsonAsync<PairConfirmRequest>(ctx);
        var fingerprint = FingerprintOf(ctx);
        var name = req is null ? null : _pairing.Confirm(fingerprint, req.PairingId);
        if (name is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var device = _config.AddDevice(fingerprint, name);
        Log.Info("Sparowano nowe urządzenie");
        _ui.OnPaired(device);
        return Results.Json(new OkResponse(true), Json.Options);
    }

    // ---------- kanał sterujący ----------

    private async Task WebSocketEndpoint(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await Hub.HandleAsync(socket, DeviceOf(ctx), ctx.RequestAborted);
    }

    // ---------- telefon → komputer ----------

    private async Task<IResult> IncomingOffer(HttpContext ctx)
    {
        var req = await ReadJsonAsync<IncomingOfferRequest>(ctx);
        if (req?.Files is not { Count: > 0 and <= ProtocolInfo.MaxFilesPerOffer } files
            || files.Any(f => f is null || f.Size < 0 || string.IsNullOrEmpty(f.Name) || f.Name.Length > ProtocolInfo.MaxRawNameLength))
            return Results.BadRequest();

        long total;
        try { total = files.Aggregate(0L, (sum, f) => checked(sum + f.Size)); }
        catch (OverflowException) { return Results.BadRequest(); }

        var device = DeviceOf(ctx);
        var transferFiles = files.Select(f => new TransferFile { Name = f.Name, Size = f.Size, SafeName = FileNameSanitizer.ForWindows(f.Name) }).ToList();
        var display = transferFiles.Select(f => new FileEntry(f.SafeName!, f.Size)).ToList();

        IncomingDecision decision;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
        {
            timeout.CancelAfter(ProtocolInfo.DecisionTimeout);
            try
            {
                decision = await _ui.AskIncomingAsync(new IncomingRequest(device, display, total), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new IncomingOfferResponse(false, Reason: "timeout"), Json.Options);
            }
        }

        if (!decision.Accepted || decision.Folder is null || !Directory.Exists(decision.Folder))
            return Results.Json(new IncomingOfferResponse(false, Reason: "rejected"), Json.Options);
        if (FreeSpace(decision.Folder) is { } free && free < total)
            return Results.Json(new IncomingOfferResponse(false, Reason: "insufficient_space"), Json.Options);

        var transfer = new IncomingTransfer(device, decision.Folder, transferFiles);
        transfer.Observer = _ui.CreateIncomingObserver(transfer);
        transfer.Finished += t =>
        {
            _incoming.TryRemove(t.Id, out _);
            DeleteParts((IncomingTransfer)t);
        };
        _incoming[transfer.Id] = transfer;
        transfer.SetStatus(TransferStatus.InProgress, "Odbieranie…");
        return Results.Json(new IncomingOfferResponse(true, transfer.Id), Json.Options);
    }

    private async Task<IResult> IncomingContent(HttpContext ctx, string id, int index)
    {
        if (FindIncoming(ctx, id, index) is not { } found)
            return Results.NotFound();
        var (transfer, file) = found;
        if (file.Done)
            return Results.Conflict();
        if (ctx.Request.ContentLength is { } length && length != file.Size)
            return Results.BadRequest();

        transfer.RestartFile(file);
        DeletePart(file);
        var part = Path.Combine(transfer.Folder, $"{file.SafeName}.{transfer.Id[..8]}.{index}.pbpart");
        file.LocalPath = part;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, transfer.Token);
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                file.Sha256 = await CopyWithHashAsync(ctx.Request.Body, output, file, transfer, cts.Token);
            return Results.NoContent();
        }
        catch (InvalidDataException)
        {
            DeletePart(file);
            return Results.BadRequest();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or BadHttpRequestException)
        {
            DeletePart(file);
            if (transfer.Token.IsCancellationRequested)
                ctx.Abort();
            return Results.Empty;
        }
    }

    private async Task<IResult> IncomingCommit(HttpContext ctx, string id, int index)
    {
        var req = await ReadJsonAsync<HashRequest>(ctx);
        if (req is null)
            return Results.BadRequest();
        if (FindIncoming(ctx, id, index) is not { } found)
            return Results.NotFound();
        var (transfer, file) = found;
        if (file.Done || file.Sha256 is null || file.LocalPath is null)
            return Results.Conflict();

        if (!HashEquals(file.Sha256, req.Sha256))
        {
            DeletePart(file);
            transfer.MarkFileDone(file, ok: false);
            return Results.Json(new CommitResponse(false), Json.Options);
        }

        try
        {
            file.SavedAs = UniqueFile.MoveToUnique(file.LocalPath, transfer.Folder, file.SafeName!);
            file.LocalPath = null;
        }
        catch (IOException e)
        {
            Log.Error("Nie można zapisać odebranego pliku", e);
            DeletePart(file);
            transfer.MarkFileDone(file, ok: false);
            return Results.Json(new CommitResponse(false), Json.Options);
        }
        transfer.MarkFileDone(file, ok: true);
        return Results.Json(new CommitResponse(true, file.SavedAs), Json.Options);
    }

    private IResult IncomingCancel(HttpContext ctx, string id)
    {
        if (!_incoming.TryGetValue(id, out var transfer) || transfer.Device.Fingerprint != DeviceOf(ctx).Fingerprint)
            return Results.NotFound();
        transfer.Cancel("Anulowano na telefonie");
        return Results.Json(new OkResponse(true), Json.Options);
    }

    private (IncomingTransfer, TransferFile)? FindIncoming(HttpContext ctx, string id, int index)
    {
        if (!_incoming.TryGetValue(id, out var transfer) || transfer.Device.Fingerprint != DeviceOf(ctx).Fingerprint
            || transfer.IsFinished || index < 0 || index >= transfer.Files.Count)
            return null;
        return (transfer, transfer.Files[index]);
    }

    // ---------- komputer → telefon ----------

    private async Task OutgoingContent(HttpContext ctx, string id, int index)
    {
        var transfer = Hub.FindOutgoing(id, DeviceOf(ctx));
        if (transfer is null || transfer.IsFinished || index < 0 || index >= transfer.Files.Count)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var file = transfer.Files[index];
        if (transfer.Status != TransferStatus.InProgress)
            transfer.SetStatus(TransferStatus.InProgress, "Telefon pobiera pliki…");
        transfer.RestartFile(file);

        FileStream input;
        try
        {
            input = new FileStream(file.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            transfer.MarkFileDone(file, ok: false);
            ctx.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        await using (input)
        {
            if (input.Length != file.Size)
            {
                transfer.MarkFileDone(file, ok: false);
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = file.Size;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, transfer.Token);
            try
            {
                file.Sha256 = await CopyWithHashAsync(input, ctx.Response.Body, file, transfer, cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException)
            {
                ctx.Abort();
            }
        }
    }

    private async Task<IResult> OutgoingDone(HttpContext ctx, string id, int index)
    {
        var req = await ReadJsonAsync<HashRequest>(ctx);
        var transfer = Hub.FindOutgoing(id, DeviceOf(ctx));
        if (req is null || transfer is null || index < 0 || index >= transfer.Files.Count)
            return Results.NotFound();

        var file = transfer.Files[index];
        var ok = file.Sha256 is not null && HashEquals(file.Sha256, req.Sha256);
        transfer.MarkFileDone(file, ok);
        return Results.Json(new OkResponse(ok), Json.Options);
    }

    // ---------- pomocnicze ----------

    private static async Task<string> CopyWithHashAsync(Stream source, Stream target, TransferFile file, Transfer transfer, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                total += read;
                if (total > file.Size)
                    throw new InvalidDataException("Strumień dłuższy niż zadeklarowany rozmiar.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                file.BytesTransferred += read;
                transfer.AddProgress(read);
            }
            if (total != file.Size)
                throw new InvalidDataException("Strumień krótszy niż zadeklarowany rozmiar.");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ProtocolInfo.MaxJsonBody;
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(Json.Options, ctx.RequestAborted);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static bool HashEquals(string expected, string? actual) =>
        actual is not null && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeDeviceName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 64)
            clean = clean[..64];
        return clean.Length == 0 ? "Telefon" : clean;
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null; // np. ścieżka sieciowa – sprawdzenie pomijamy
        }
    }

    private static void DeleteParts(IncomingTransfer transfer)
    {
        foreach (var file in transfer.Files)
            DeletePart(file);
    }

    private static void DeletePart(TransferFile file)
    {
        if (file.LocalPath is not { } path)
            return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Nie można usunąć pliku tymczasowego", e); }
        file.LocalPath = null;
    }

    private void Sweep()
    {
        var limit = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var transfer in _incoming.Values.Concat<Transfer>(Hub.ActiveOutgoing))
        {
            if (transfer.Status == TransferStatus.InProgress && transfer.LastActivity < limit)
                transfer.SetStatus(TransferStatus.Failed, "Przekroczono czas oczekiwania na telefon");
        }
    }
}
