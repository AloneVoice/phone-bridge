using PhoneBridge.Core.Files;

namespace PhoneBridge.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("zdjęcie.jpg", "zdjęcie.jpg")]
    [InlineData("a/b\\c.txt", "a_b_c.txt")]
    [InlineData("..\\..\\Windows\\win.ini", ".._.._Windows_win.ini")]
    [InlineData("..", "plik")]
    [InlineData("   ", "plik")]
    [InlineData("nazwa. . .", "nazwa")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1 .log", "_LPT1 .log")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("faktura‮gpj.exe", "faktura_gpj.exe")]
    public void Sanitizes_names_for_windows(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.ForWindows(input));
    }

    [Theory]
    [InlineData(1, "1 plik")]
    [InlineData(3, "3 pliki")]
    [InlineData(5, "5 plików")]
    [InlineData(12, "12 plików")]
    [InlineData(22, "22 pliki")]
    [InlineData(112, "112 plików")]
    public void Polish_plural_for_status_messages(int count, string expected)
    {
        Assert.Equal(expected, PhoneBridge.Core.Server.Transfer.FilesPlural(count));
    }

    [Fact]
    public void Truncates_long_names_keeping_extension()
    {
        var result = FileNameSanitizer.ForWindows(new string('x', 500) + ".jpeg");
        Assert.Equal(200, result.Length);
        Assert.EndsWith(".jpeg", result);
    }

    [Fact]
    public void Truncation_does_not_split_surrogate_pairs()
    {
        var result = FileNameSanitizer.ForWindows(new string('x', 194) + "😀😀😀😀.png");
        Assert.False(char.IsHighSurrogate(result[^5]));
        Assert.EndsWith(".png", result);
    }

    [Fact]
    public void MoveToUnique_never_overwrites()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "old");
        File.WriteAllText(Path.Combine(dir.Path, "a (1).txt"), "old1");
        var source = Path.Combine(dir.Path, "incoming.part");
        File.WriteAllText(source, "new");

        var saved = UniqueFile.MoveToUnique(source, dir.Path, "a.txt");

        Assert.Equal("a (2).txt", saved);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir.Path, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dir.Path, saved)));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Collector_walks_folders_with_relative_names()
    {
        using var dir = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(dir.Path, "Zdjęcia"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "2026"));
        File.WriteAllText(Path.Combine(root.FullName, "a.jpg"), "a");
        File.WriteAllText(Path.Combine(root.FullName, "2026", "b.jpg"), "bb");
        var single = Path.Combine(dir.Path, "c.txt");
        File.WriteAllText(single, "ccc");

        var files = LocalFileCollector.Collect([root.FullName, single, Path.Combine(dir.Path, "missing.txt")]);

        Assert.Equal(
            ["Zdjęcia/2026/b.jpg:2", "Zdjęcia/a.jpg:1", "c.txt:3"],
            files.Select(f => $"{f.RelativeName}:{f.Size}").Order(StringComparer.Ordinal));
    }
}
