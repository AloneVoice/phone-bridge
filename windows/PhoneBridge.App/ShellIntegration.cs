using System.IO;
using Microsoft.Win32;
using PhoneBridge.Core;

namespace PhoneBridge.App;

/// <summary>
/// Integracja z Eksploratorem bez uprawnień administratora: klasyczne menu kontekstowe (HKCU),
/// skrót w „Wyślij do” i autostart.
/// </summary>
internal static class ShellIntegration
{
    private const string VerbName = "PhoneBridge.Send";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PhoneBridge";
    private static readonly string[] ShellKeys = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];

    private static string SendToShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Telefon (PhoneBridge).lnk");

    public static void Register(string exe)
    {
        foreach (var shell in ShellKeys)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{shell}\{VerbName}");
            // „&t” – akcelerator: Shift+F10, potem T
            verb.SetValue("", "Wyślij na &telefon");
            verb.SetValue("Icon", $"\"{exe}\",0");
            // bez limitu 15 zaznaczonych elementów
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue("", $"\"{exe}\" --send \"%1\"");
        }
        CreateSendToShortcut(exe);
    }

    public static void Unregister()
    {
        foreach (var shell in ShellKeys)
            Registry.CurrentUser.DeleteSubKeyTree($@"{shell}\{VerbName}", throwOnMissingSubKey: false);
        try { File.Delete(SendToShortcut); }
        catch (IOException) { }
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static bool IsAutostartEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunValue) is not null;
    }

    public static void SetAutostart(string exe, bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            run.SetValue(RunValue, $"\"{exe}\" --background");
        else
            run.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static void CreateSendToShortcut(string exe)
    {
        // „Wyślij do” przekazuje wszystkie zaznaczone pliki w jednym wywołaniu
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(SendToShortcut);
            link.TargetPath = exe;
            link.Arguments = "--send";
            link.IconLocation = $"{exe},0";
            link.Description = "Wyślij na telefon przez PhoneBridge";
            link.Save();
        }
        catch (Exception e)
        {
            Log.Error("Nie można utworzyć skrótu Wyślij do", e);
        }
    }
}
