using System.Security.Cryptography;
using PhoneBridge.Core.Security;

namespace PhoneBridge.Core.Server;

public enum PairStartError { None, Busy, CoolingDown, InvalidRequest }

/// <summary>
/// Sesja parowania commit/reveal z porównaniem kodu (docs/protocol.md). Jedna sesja naraz.
/// </summary>
public sealed class PairingManager
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private enum State { Started, Revealed, Accepted }

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string ClientFingerprint { get; init; }
        public required string DeviceName { get; init; }
        public required byte[] Commitment { get; init; }
        public required byte[] ServerNonce { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public State State { get; set; }
    }

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private Session? _session;
    private DateTimeOffset _cooldownUntil;

    public PairingManager(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public (PairStartError Error, string? PairingId, string? ServerNonce) Start(string clientFingerprint, string deviceName, string? commitmentHex)
    {
        if (!Fingerprint.IsValidHex(commitmentHex))
            return (PairStartError.InvalidRequest, null, null);

        lock (_sync)
        {
            var now = _time.GetUtcNow();
            if (now < _cooldownUntil)
                return (PairStartError.CoolingDown, null, null);
            if (_session is { } current && current.Expires > now && current.ClientFingerprint != clientFingerprint)
                return (PairStartError.Busy, null, null);

            var nonce = RandomNumberGenerator.GetBytes(32);
            _session = new Session
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ClientFingerprint = clientFingerprint,
                DeviceName = deviceName,
                Commitment = Convert.FromHexString(commitmentHex!),
                ServerNonce = nonce,
                Expires = now + Lifetime,
            };
            return (PairStartError.None, _session.Id, Convert.ToHexStringLower(nonce));
        }
    }

    /// <summary>Sprawdza commitment i zwraca kod do pokazania użytkownikowi albo null.</summary>
    public (string Code, string DeviceName)? Reveal(string clientFingerprint, string? pairingId, string? nonceHex, string serverFingerprint)
    {
        if (!Fingerprint.IsValidHex(nonceHex))
            return null;
        var nonce = Convert.FromHexString(nonceHex!);

        lock (_sync)
        {
            var s = Find(clientFingerprint, pairingId, State.Started);
            if (s is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(nonce), s.Commitment))
                return null;
            s.State = State.Revealed;
            return (Sas.Compute(serverFingerprint, clientFingerprint, nonce, s.ServerNonce), s.DeviceName);
        }
    }

    public void SetDecision(string clientFingerprint, string pairingId, bool accepted)
    {
        lock (_sync)
        {
            var s = Find(clientFingerprint, pairingId, State.Revealed);
            if (s is null)
                return;
            if (accepted)
            {
                s.State = State.Accepted;
            }
            else
            {
                _session = null;
                _cooldownUntil = _time.GetUtcNow() + Cooldown;
            }
        }
    }

    /// <summary>Kończy sesję zaakceptowaną na komputerze. Zwraca nazwę urządzenia do zapisania lub null.</summary>
    public string? Confirm(string clientFingerprint, string? pairingId)
    {
        lock (_sync)
        {
            var s = Find(clientFingerprint, pairingId, State.Accepted);
            if (s is null)
                return null;
            _session = null;
            return s.DeviceName;
        }
    }

    private Session? Find(string clientFingerprint, string? pairingId, State state)
    {
        var s = _session;
        if (s is null || s.Expires <= _time.GetUtcNow())
            return null;
        return s.Id == pairingId && s.ClientFingerprint == clientFingerprint && s.State == state ? s : null;
    }
}
