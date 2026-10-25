using PhoneBridge.Core.Config;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Server;

public sealed record IncomingRequest(TrustedDevice Device, IReadOnlyList<FileEntry> Files, long TotalBytes);

public sealed record IncomingDecision(bool Accepted, string? Folder)
{
    public static readonly IncomingDecision Rejected = new(false, null);
}

public sealed record PairingRequest(string DeviceName, string Code, string RemoteAddress);

/// <summary>Interakcje serwera z użytkownikiem. Wywoływane z wątków puli – implementacja przełącza się na wątek UI.</summary>
public interface IBridgeUi
{
    /// <summary>Okno akceptacji odbioru z wyborem folderu. Anulowanie tokenu = timeout lub rezygnacja telefonu.</summary>
    Task<IncomingDecision> AskIncomingAsync(IncomingRequest request, CancellationToken cancellationToken);

    /// <summary>Okno „Sparować z telefonem? Kod: 123 456”.</summary>
    Task<bool> AskPairingAsync(PairingRequest request, CancellationToken cancellationToken);

    ITransferObserver CreateIncomingObserver(IncomingTransfer transfer);

    void OnPaired(TrustedDevice device);
}
