using System.ComponentModel;
using System.Diagnostics;
using PhoneBridge.Core;

namespace PhoneBridge.App;

/// <summary>Reguła zapory (jednorazowo z UAC) i wykrywanie sieci publicznej.</summary>
internal static class NetworkSetup
{
    private const string RuleName = "PhoneBridge";

    public static bool FirewallRuleExists(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" verbose")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Dodaje regułę tylko dla sieci prywatnych i lokalnej podsieci. Wymaga zgody UAC.</summary>
    public static bool AddFirewallRules(string exe, int tcpPort, int udpPort)
    {
        string Rule(string protocol, int port) =>
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" " +
            $"protocol={protocol} localport={port} profile=private remoteip=localsubnet enable=yes";

        var command = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" & {Rule("TCP", tcpPort)} && {Rule("UDP", udpPort)}";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // użytkownik odmówił w oknie UAC
        }
    }

    /// <summary>Windows 11 domyślnie oznacza nowe sieci jako publiczne – wtedy reguła dla sieci prywatnych nie działa.</summary>
    public static bool IsConnectedToPublicNetwork()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null)
                return false;
            dynamic manager = Activator.CreateInstance(type)!;
            const int connected = 1;
            foreach (dynamic network in manager.GetNetworks(connected))
            {
                if ((int)network.GetCategory() == 0)
                    return true;
            }
        }
        catch (Exception e)
        {
            Log.Error("Nie można sprawdzić profilu sieci", e);
        }
        return false;
    }
}
