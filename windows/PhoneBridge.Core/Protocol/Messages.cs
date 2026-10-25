using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneBridge.Core.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const int DiscoveryPort = 47801;
    public const int MaxFilesPerOffer = 10_000;
    public const int MaxPathDepth = 32;
    public const int MaxRawNameLength = 1024;
    public const long MaxJsonBody = 4 * 1024 * 1024;
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(120);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record FileEntry(string Name, long Size);

public sealed record PairStartRequest(string DeviceName, string Commitment);
public sealed record PairStartResponse(string PairingId, string ServerNonce, string ServerName);
public sealed record PairRevealRequest(string PairingId, string Nonce);
public sealed record PairRevealResponse(bool Accepted);
public sealed record PairConfirmRequest(string PairingId);
public sealed record OkResponse(bool Ok);

public sealed record IncomingOfferRequest(List<FileEntry> Files);
public sealed record IncomingOfferResponse(bool Accepted, string? Id = null, string? Reason = null);
public sealed record HashRequest(string Sha256);
public sealed record CommitResponse(bool Ok, string? SavedAs = null);

public sealed record DiscoveryReply(int Pb, string Name, int Port, string Fp);

/// <summary>Ramka kanału WebSocket. Pola zależą od <see cref="Type"/>.</summary>
public sealed record WsMessage(
    string Type,
    string? Id = null,
    List<FileEntry>? Files = null,
    string? ServerName = null,
    string? DeviceName = null,
    int? Protocol = null);

public static class WsTypes
{
    public const string Hello = "hello";
    public const string Offer = "offer";
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Cancel = "cancel";
}
