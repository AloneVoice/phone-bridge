using System.Security.Cryptography;
using PhoneBridge.Core.Security;
using PhoneBridge.Core.Server;

namespace PhoneBridge.Tests;

public class PairingTests
{
    private static readonly string ServerFp = new('a', 64);
    private static readonly string ClientFp = new('b', 64);

    [Fact]
    public void Sas_matches_cross_platform_test_vector()
    {
        // Ten sam wektor sprawdza test Androida (SasTest.kt) – obie strony muszą liczyć identyczny kod.
        var phoneNonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var serverNonce = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

        var code = Sas.Compute(ServerFp, ClientFp, phoneNonce, serverNonce);

        Assert.Equal(TestVectors.Sas, code);
        Assert.Equal($"{code[..3]} {code[3..]}", Sas.Format(code));
    }

    [Fact]
    public void Full_session_returns_device_name()
    {
        var pairing = new PairingManager();
        var nonce = RandomNumberGenerator.GetBytes(32);

        var (error, id, _) = pairing.Start(ClientFp, "Pixel", Commit(nonce));
        Assert.Equal(PairStartError.None, error);
        Assert.NotNull(pairing.Reveal(ClientFp, id, Convert.ToHexString(nonce), ServerFp));
        Assert.Null(pairing.Confirm(ClientFp, id)); // bez decyzji na komputerze nie wolno potwierdzić
        pairing.SetDecision(ClientFp, id!, accepted: true);
        Assert.Equal("Pixel", pairing.Confirm(ClientFp, id));
        Assert.Null(pairing.Confirm(ClientFp, id)); // sesja jednorazowa
    }

    [Fact]
    public void Reveal_with_nonce_other_than_committed_fails()
    {
        var pairing = new PairingManager();
        var (_, id, _) = pairing.Start(ClientFp, "Pixel", Commit(RandomNumberGenerator.GetBytes(32)));

        Assert.Null(pairing.Reveal(ClientFp, id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), ServerFp));
    }

    [Fact]
    public void Session_is_bound_to_client_certificate()
    {
        var pairing = new PairingManager();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var (_, id, _) = pairing.Start(ClientFp, "Pixel", Commit(nonce));

        Assert.Null(pairing.Reveal(new string('c', 64), id, Convert.ToHexString(nonce), ServerFp));
    }

    [Fact]
    public void Second_device_is_blocked_while_session_active_and_after_reject()
    {
        var time = new ManualTime();
        var pairing = new PairingManager(time);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var (_, id, _) = pairing.Start(ClientFp, "Pixel", Commit(nonce));

        Assert.Equal(PairStartError.Busy, pairing.Start(new string('c', 64), "Intruz", Commit(nonce)).Error);

        pairing.Reveal(ClientFp, id, Convert.ToHexString(nonce), ServerFp);
        pairing.SetDecision(ClientFp, id!, accepted: false);
        Assert.Equal(PairStartError.CoolingDown, pairing.Start(ClientFp, "Pixel", Commit(nonce)).Error);

        time.Advance(PairingManager.Cooldown);
        Assert.Equal(PairStartError.None, pairing.Start(ClientFp, "Pixel", Commit(nonce)).Error);
    }

    [Fact]
    public void Session_expires()
    {
        var time = new ManualTime();
        var pairing = new PairingManager(time);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var (_, id, _) = pairing.Start(ClientFp, "Pixel", Commit(nonce));

        time.Advance(PairingManager.Lifetime);

        Assert.Null(pairing.Reveal(ClientFp, id, Convert.ToHexString(nonce), ServerFp));
    }

    private static string Commit(byte[] nonce) => Convert.ToHexStringLower(SHA256.HashData(nonce));

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

internal static class TestVectors
{
    public const string Sas = "747696";
}
