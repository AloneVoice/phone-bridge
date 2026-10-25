using System.Security.Cryptography;
using PhoneBridge.Core.Config;
using PhoneBridge.Core.Protocol;

namespace PhoneBridge.Core.Server;

public enum TransferStatus { Queued, WaitingForAccept, InProgress, Completed, Failed, Rejected, Cancelled }

public enum TransferDirection { ToPhone, FromPhone }

public interface ITransferObserver
{
    void OnStatus(TransferStatus status, string? message);
    void OnProgress(long bytesDone, long bytesTotal);
}

public sealed class NullTransferObserver : ITransferObserver
{
    public static readonly NullTransferObserver Instance = new();
    public void OnStatus(TransferStatus status, string? message) { }
    public void OnProgress(long bytesDone, long bytesTotal) { }
}

public sealed class TransferFile
{
    public required string Name { get; init; }
    public required long Size { get; init; }

    /// <summary>Wysyłanie: plik źródłowy. Odbieranie: plik tymczasowy *.pbpart.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Odbieranie: bezpieczna nazwa docelowa.</summary>
    public string? SafeName { get; init; }

    public string? Sha256 { get; set; }
    public long BytesTransferred { get; set; }
    public bool Done { get; set; }
    public bool Ok { get; set; }
    public string? SavedAs { get; set; }
}

public abstract class Transfer
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private long _bytesDone;
    private int _filesDone;
    private int _filesFailed;

    protected Transfer(TrustedDevice device, TransferDirection direction, IReadOnlyList<TransferFile> files)
    {
        Device = device;
        Direction = direction;
        Files = files;
        TotalBytes = files.Sum(f => f.Size);
        LastActivity = DateTimeOffset.UtcNow;
    }

    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public TrustedDevice Device { get; }
    public TransferDirection Direction { get; }
    public IReadOnlyList<TransferFile> Files { get; }
    public long TotalBytes { get; }
    public TransferStatus Status { get; private set; } = TransferStatus.Queued;
    public DateTimeOffset LastActivity { get; private set; }
    public ITransferObserver Observer { get; set; } = NullTransferObserver.Instance;
    public CancellationToken Token => _cts.Token;

    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed
        or TransferStatus.Rejected or TransferStatus.Cancelled;

    public event Action<Transfer>? Finished;

    public List<FileEntry> ToEntries() => Files.Select(f => new FileEntry(f.Name, f.Size)).ToList();

    public bool SetStatus(TransferStatus status, string? message = null)
    {
        lock (_sync)
        {
            if (IsFinished)
                return false;
            Status = status;
            LastActivity = DateTimeOffset.UtcNow;
        }

        Observer.OnStatus(status, message);
        if (IsFinished)
        {
            if (status != TransferStatus.Completed)
                _cts.Cancel();
            Finished?.Invoke(this);
        }
        return true;
    }

    public void Cancel(string message) => SetStatus(TransferStatus.Cancelled, message);

    public void AddProgress(long bytes)
    {
        LastActivity = DateTimeOffset.UtcNow;
        Observer.OnProgress(Interlocked.Add(ref _bytesDone, bytes), TotalBytes);
    }

    /// <summary>Przy ponownej próbie przesłania pliku cofamy wcześniej naliczony postęp.</summary>
    public void RestartFile(TransferFile file)
    {
        AddProgress(-file.BytesTransferred);
        file.BytesTransferred = 0;
        file.Sha256 = null;
    }

    public void MarkFileDone(TransferFile file, bool ok)
    {
        int done, failed;
        lock (_sync)
        {
            if (file.Done)
                return;
            file.Done = true;
            file.Ok = ok;
            done = ++_filesDone;
            failed = ok ? _filesFailed : ++_filesFailed;
        }

        if (done < Files.Count)
            return;
        if (failed == 0)
            SetStatus(TransferStatus.Completed, Files.Count == 1 ? "Przesłano plik" : $"Przesłano {FilesPlural(Files.Count)}");
        else
            SetStatus(TransferStatus.Failed, $"Błąd weryfikacji {failed} z {Files.Count} plików");
    }

    /// <summary>2 pliki, 5 plików, 22 pliki, 112 plików (mianownik/biernik).</summary>
    internal static string FilesPlural(int count)
    {
        var last = count % 10;
        var lastTwo = count % 100;
        return count == 1 ? "1 plik"
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? $"{count} pliki"
            : $"{count} plików";
    }
}

public sealed class OutgoingTransfer : Transfer
{
    public OutgoingTransfer(TrustedDevice device, IReadOnlyList<TransferFile> files)
        : base(device, TransferDirection.ToPhone, files) { }
}

public sealed class IncomingTransfer : Transfer
{
    public IncomingTransfer(TrustedDevice device, string folder, IReadOnlyList<TransferFile> files)
        : base(device, TransferDirection.FromPhone, files)
    {
        Folder = folder;
    }

    public string Folder { get; }
}
