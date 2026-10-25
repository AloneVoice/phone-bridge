using System.Globalization;

namespace PhoneBridge.App;

internal static class Format
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", Polish) + " " + units[unit];
    }

    /// <summary>1 plik, 2 pliki, 5 plików, 22 pliki, 112 plików.</summary>
    public static string Files(int count)
    {
        var lastTwo = count % 100;
        var last = count % 10;
        var word = count == 1 ? "plik"
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "pliki"
            : "plików";
        return $"{count} {word}";
    }

    public static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min {time.Seconds} s"
        : $"{Math.Max(1, time.Seconds)} s";
}
