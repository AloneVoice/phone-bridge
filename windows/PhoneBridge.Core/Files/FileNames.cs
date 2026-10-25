using System.Buffers;
using System.Text;

namespace PhoneBridge.Core.Files;

public static class FileNameSanitizer
{
    public const string Fallback = "plik";
    private const int MaxLength = 200;

    private static readonly SearchValues<char> Invalid = SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Zamienia nazwę otrzymaną od drugiej strony na bezpieczną nazwę pliku Windows (bez separatorów ścieżki).
    /// </summary>
    public static string ForWindows(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Fallback;

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(IsForbidden(ch) ? '_' : ch);

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
            return Fallback;

        var dot = result.IndexOf('.');
        var stem = (dot < 0 ? result : result[..dot]).TrimEnd(' ');
        if (Reserved.Contains(stem))
            result = "_" + result;

        return Truncate(result);
    }

    private static bool IsForbidden(char ch) =>
        ch < 0x20 || ch == 0x7F || Invalid.Contains(ch)
        // znaki sterujące kierunkiem tekstu pozwalają ukryć prawdziwe rozszerzenie („gpj.exe”)
        || ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    private static string Truncate(string name)
    {
        if (name.Length <= MaxLength)
            return name;

        var ext = Path.GetExtension(name);
        if (ext.Length > 20)
            ext = "";
        var stemLength = MaxLength - ext.Length;
        if (char.IsHighSurrogate(name[stemLength - 1]))
            stemLength--;
        return name[..stemLength].TrimEnd('.', ' ') + ext;
    }
}

public static class UniqueFile
{
    /// <summary>
    /// Przenosi plik do folderu pod nazwą, która jeszcze nie istnieje ("a.txt", "a (1).txt", ...).
    /// Nigdy nie nadpisuje istniejących plików. Zwraca użytą nazwę.
    /// </summary>
    public static string MoveToUnique(string sourcePath, string folder, string fileName)
    {
        var root = Path.GetFullPath(folder);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 0; i < 10_000; i++)
        {
            var candidate = i == 0 ? fileName : $"{stem} ({i}){ext}";
            var target = Path.GetFullPath(Path.Combine(root, candidate));
            if (!string.Equals(Path.GetDirectoryName(target), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Nazwa pliku wychodzi poza folder docelowy.");
            if (File.Exists(target) || Directory.Exists(target))
                continue;
            try
            {
                File.Move(sourcePath, target, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target))
            {
                // ktoś utworzył plik w międzyczasie – próbujemy kolejnej nazwy
            }
        }
        throw new IOException("Nie można utworzyć unikalnej nazwy pliku.");
    }
}

public sealed record LocalFile(string FullPath, string RelativeName, long Size);

public static class LocalFileCollector
{
    /// <summary>
    /// Zamienia zaznaczone pliki i foldery na listę plików do wysłania.
    /// Foldery są przechodzone rekurencyjnie, a nazwy względne zachowują strukturę ("Folder/pod/plik.txt").
    /// </summary>
    public static List<LocalFile> Collect(IEnumerable<string> paths)
    {
        var result = new List<LocalFile>();
        foreach (var path in paths)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (File.Exists(full))
            {
                result.Add(new LocalFile(full, Path.GetFileName(full), new FileInfo(full).Length));
            }
            else if (Directory.Exists(full))
            {
                var dir = new DirectoryInfo(full);
                var rootName = dir.Parent is null ? FileNameSanitizer.Fallback : dir.Name;
                Walk(dir, rootName, 1, result);
            }
        }
        return result;
    }

    private static void Walk(DirectoryInfo dir, string prefix, int depth, List<LocalFile> result)
    {
        if (depth > Protocol.ProtocolInfo.MaxPathDepth || result.Count >= Protocol.ProtocolInfo.MaxFilesPerOffer)
            return;
        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                if (result.Count >= Protocol.ProtocolInfo.MaxFilesPerOffer)
                    return;
                result.Add(new LocalFile(file.FullName, $"{prefix}/{file.Name}", file.Length));
            }
            foreach (var sub in dir.EnumerateDirectories())
            {
                // dowiązania i junction-y pomijamy, żeby nie wpaść w pętlę ani nie wyjść poza zaznaczony folder
                if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Walk(sub, $"{prefix}/{sub.Name}", depth + 1, result);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Log.Info($"Pominięto niedostępny folder ({e.GetType().Name}).");
        }
    }
}
