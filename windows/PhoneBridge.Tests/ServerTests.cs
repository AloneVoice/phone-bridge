using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PhoneBridge.Core.Files;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Security;
using PhoneBridge.Core.Server;

namespace PhoneBridge.Tests;

public class ServerTests
{
    [Fact]
    public async Task Pairing_over_tls_shows_same_code_on_both_sides_and_trusts_phone()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        var nonce = RandomNumberGenerator.GetBytes(32);

        var start = await phone.ReadAsync<PairStartResponse>(await phone.PostJsonAsync("/v1/pair/start",
            new PairStartRequest("Pixel 8", Convert.ToHexStringLower(SHA256.HashData(nonce)))));
        Assert.Equal("TEST-PC", start.ServerName);

        var reveal = await phone.ReadAsync<PairRevealResponse>(await phone.PostJsonAsync("/v1/pair/reveal",
            new PairRevealRequest(start.PairingId, Convert.ToHexStringLower(nonce))));
        Assert.True(reveal.Accepted);

        // telefon liczy kod z odcisku certyfikatu widzianego w TLS – musi zgadzać się z kodem z okna na PC
        var phoneCode = Sas.Compute(server.Server.ServerFingerprint, phone.Fingerprint, nonce, Convert.FromHexString(start.ServerNonce));
        Assert.True(server.Ui.PairingRequests.TryDequeue(out var shown));
        Assert.Equal(phoneCode, shown.Code);
        Assert.Equal("Pixel 8", shown.DeviceName);

        Assert.Null(server.Config.FindDevice(phone.Fingerprint)); // zaufanie dopiero po potwierdzeniu telefonu
        var confirm = await phone.ReadAsync<OkResponse>(await phone.PostJsonAsync("/v1/pair/confirm", new PairConfirmRequest(start.PairingId)));
        Assert.True(confirm.Ok);
        Assert.NotNull(server.Config.FindDevice(phone.Fingerprint));
        Assert.Single(server.Ui.Paired);
    }

    [Fact]
    public async Task Pairing_rejected_on_pc_does_not_trust_phone()
    {
        await using var server = await TestServer.StartAsync();
        server.Ui.OnPairing = _ => Task.FromResult(false);
        using var phone = new TestPhone(server);
        var nonce = RandomNumberGenerator.GetBytes(32);

        var start = await phone.ReadAsync<PairStartResponse>(await phone.PostJsonAsync("/v1/pair/start",
            new PairStartRequest("Pixel", Convert.ToHexStringLower(SHA256.HashData(nonce)))));
        var reveal = await phone.ReadAsync<PairRevealResponse>(await phone.PostJsonAsync("/v1/pair/reveal",
            new PairRevealRequest(start.PairingId, Convert.ToHexStringLower(nonce))));
        var confirm = await phone.PostJsonAsync("/v1/pair/confirm", new PairConfirmRequest(start.PairingId));

        Assert.False(reveal.Accepted);
        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Null(server.Config.FindDevice(phone.Fingerprint));
    }

    [Fact]
    public async Task Unpaired_phone_cannot_send_receive_or_open_channel()
    {
        await using var server = await TestServer.StartAsync();
        server.Ui.OnIncoming = (_, _) => throw new InvalidOperationException("Nie powinno zapytać użytkownika");
        using var phone = new TestPhone(server);

        var offer = await phone.PostJsonAsync("/v1/incoming", new IncomingOfferRequest([new FileEntry("a.txt", 1)]));
        var download = await phone.Http.GetAsync("/v1/outgoing/abc/0");

        Assert.Equal(HttpStatusCode.Forbidden, offer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(phone.ConnectWebSocketAsync);
    }

    [Fact]
    public async Task Phone_rejects_server_with_unexpected_certificate()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server, pinnedFingerprint: new string('0', 64));

        await Assert.ThrowsAsync<HttpRequestException>(() => phone.Http.GetAsync("/v1/outgoing/x/0"));
    }

    [Fact]
    public async Task Incoming_files_are_verified_and_saved_without_overwriting()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        phone.PairDirectly(server);
        var folder = server.DownloadFolder;
        File.WriteAllText(Path.Combine(folder, "notatka.txt"), "stara");
        IncomingRequest? asked = null;
        server.Ui.OnIncoming = (req, _) => { asked = req; return Task.FromResult(new IncomingDecision(true, folder)); };

        byte[][] contents = [RandomNumberGenerator.GetBytes(3 * 256 * 1024 + 17), "nowa"u8.ToArray()];
        var offer = await phone.ReadAsync<IncomingOfferResponse>(await phone.PostJsonAsync("/v1/incoming",
            new IncomingOfferRequest([new FileEntry("..\\..\\obraz.bin", contents[0].Length), new FileEntry("notatka.txt", contents[1].Length)])));

        Assert.True(offer.Accepted);
        Assert.Equal(contents[0].Length + contents[1].Length, asked!.TotalBytes);

        var saved = new List<string>();
        for (var i = 0; i < contents.Length; i++)
        {
            var put = await phone.Http.PutAsync($"/v1/incoming/{offer.Id}/{i}", new ByteArrayContent(contents[i]));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            var commit = await phone.ReadAsync<CommitResponse>(await phone.PostJsonAsync($"/v1/incoming/{offer.Id}/{i}/commit",
                new HashRequest(Convert.ToHexStringLower(SHA256.HashData(contents[i])))));
            Assert.True(commit.Ok);
            saved.Add(commit.SavedAs!);
        }

        Assert.Equal([".._.._obraz.bin", "notatka (1).txt"], saved);
        Assert.Equal(contents[0], File.ReadAllBytes(Path.Combine(folder, saved[0])));
        Assert.Equal("stara", File.ReadAllText(Path.Combine(folder, "notatka.txt")));
        Assert.Empty(Directory.GetFiles(folder, "*.pbpart"));
        Assert.Equal(TransferStatus.Completed, await server.Ui.IncomingObserver.Finished.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Incoming_file_with_wrong_hash_is_discarded()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        phone.PairDirectly(server);
        var folder = server.DownloadFolder;
        server.Ui.OnIncoming = (_, _) => Task.FromResult(new IncomingDecision(true, folder));

        var offer = await phone.ReadAsync<IncomingOfferResponse>(await phone.PostJsonAsync("/v1/incoming",
            new IncomingOfferRequest([new FileEntry("a.txt", 5)])));
        await phone.Http.PutAsync($"/v1/incoming/{offer.Id}/0", new ByteArrayContent("hello"u8.ToArray()));
        var commit = await phone.ReadAsync<CommitResponse>(await phone.PostJsonAsync($"/v1/incoming/{offer.Id}/0/commit",
            new HashRequest(new string('0', 64))));

        Assert.False(commit.Ok);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Equal(TransferStatus.Failed, await server.Ui.IncomingObserver.Finished.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Incoming_body_larger_than_declared_is_refused()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        phone.PairDirectly(server);
        var folder = server.DownloadFolder;
        server.Ui.OnIncoming = (_, _) => Task.FromResult(new IncomingDecision(true, folder));

        var offer = await phone.ReadAsync<IncomingOfferResponse>(await phone.PostJsonAsync("/v1/incoming",
            new IncomingOfferRequest([new FileEntry("a.txt", 2)])));
        var put = await phone.Http.PutAsync($"/v1/incoming/{offer.Id}/0", new ByteArrayContent("za długie"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Incoming_offer_rejected_by_user()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        phone.PairDirectly(server);

        var offer = await phone.ReadAsync<IncomingOfferResponse>(await phone.PostJsonAsync("/v1/incoming",
            new IncomingOfferRequest([new FileEntry("a.txt", 1)])));

        Assert.False(offer.Accepted);
        Assert.Equal("rejected", offer.Reason);
    }

    [Fact]
    public async Task Outgoing_offer_is_delivered_over_websocket_and_downloaded()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        var device = phone.PairDirectly(server);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "raport.pdf");
        var content = RandomNumberGenerator.GetBytes(1024 * 1024 + 3);
        File.WriteAllBytes(path, content);
        var observer = new RecordingObserver();

        // oferta utworzona zanim telefon się połączył – ma dotrzeć po połączeniu
        var transfer = server.Server.Hub.CreateOffer(device, LocalFileCollector.Collect([path]), observer);
        using var ws = await phone.ConnectWebSocketAsync();

        var hello = await TestPhone.ReceiveAsync(ws);
        var offer = await TestPhone.ReceiveAsync(ws);
        Assert.Equal(WsTypes.Hello, hello.Type);
        Assert.Equal(WsTypes.Offer, offer.Type);
        Assert.Equal(transfer.Id, offer.Id);
        Assert.Equal("raport.pdf", offer.Files![0].Name);

        await TestPhone.SendAsync(ws, new WsMessage(WsTypes.Accept, offer.Id));
        var downloaded = await phone.Http.GetByteArrayAsync($"/v1/outgoing/{offer.Id}/0");
        var done = await phone.ReadAsync<OkResponse>(await phone.PostJsonAsync($"/v1/outgoing/{offer.Id}/0/done",
            new HashRequest(Convert.ToHexStringLower(SHA256.HashData(downloaded)))));

        Assert.Equal(content, downloaded);
        Assert.True(done.Ok);
        Assert.Equal(TransferStatus.Completed, await observer.Finished.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(content.Length, observer.LastProgress);
    }

    [Fact]
    public async Task Other_paired_phone_cannot_download_someone_elses_offer()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        using var other = new TestPhone(server);
        var device = phone.PairDirectly(server);
        other.PairDirectly(server);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "tajne.txt");
        File.WriteAllText(path, "tajne");

        var transfer = server.Server.Hub.CreateOffer(device, LocalFileCollector.Collect([path]), new RecordingObserver());
        var response = await other.Http.GetAsync($"/v1/outgoing/{transfer.Id}/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Phone_reject_over_websocket_finishes_offer()
    {
        await using var server = await TestServer.StartAsync();
        using var phone = new TestPhone(server);
        var device = phone.PairDirectly(server);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.txt");
        File.WriteAllText(path, "a");
        var observer = new RecordingObserver();

        using var ws = await phone.ConnectWebSocketAsync();
        await TestPhone.ReceiveAsync(ws); // hello
        var transfer = server.Server.Hub.CreateOffer(device, LocalFileCollector.Collect([path]), observer);
        var offer = await TestPhone.ReceiveAsync(ws);
        await TestPhone.SendAsync(ws, new WsMessage(WsTypes.Reject, offer.Id));

        Assert.Equal(TransferStatus.Rejected, await observer.Finished.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(transfer.Id, offer.Id);
    }
}
