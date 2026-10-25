namespace PhoneBridge.Core;

/// <summary>
/// Prosty dziennik diagnostyczny. Domyślnie nie zapisujemy nazw plików ani ich treści.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private const long MaxSize = 2 * 1024 * 1024;

    public static string? FilePath { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        var path = FilePath;
        if (path is null)
            return;
        try
        {
            lock (Sync)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // dziennik nie może zatrzymać aplikacji
        }
    }
}
