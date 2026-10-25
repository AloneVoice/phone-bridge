using System.Diagnostics;
using System.Windows;

namespace PhoneBridge.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var exe = Environment.ProcessPath!;
        switch (args.FirstOrDefault())
        {
            case "--send":
                return Send(exe, args[1..]);
            case "--register":
                ShellIntegration.Register(exe);
                ShellIntegration.SetAutostart(exe, true);
                return 0;
            case "--unregister":
                ShellIntegration.Unregister();
                return 0;
        }

        using var mutex = new Mutex(true, @"Local\PhoneBridge.Agent", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Ipc.TrySend(new IpcMessage(IpcMessage.Show, []));
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Theme());
        var agent = new Agent(app, exe, startedInBackground: args.Contains("--background"));
        app.Startup += async (_, _) => await agent.StartAsync();
        app.Exit += (_, _) => agent.Dispose();
        return app.Run();
    }

    /// <summary>
    /// Wywołanie z menu kontekstowego: przekazuje ścieżki do działającego agenta (uruchamia go w razie potrzeby).
    /// Explorer uruchamia osobny proces dla każdego zaznaczonego pliku – agent skleja je w jedną ofertę.
    /// </summary>
    private static int Send(string exe, string[] paths)
    {
        var message = new IpcMessage(IpcMessage.Send, paths);
        if (Ipc.TrySend(message))
            return 0;

        try
        {
            Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // inny proces z tej samej selekcji mógł już uruchomić agenta
        }

        for (var i = 0; i < 60; i++)
        {
            Thread.Sleep(250);
            if (Ipc.TrySend(message))
                return 0;
        }

        MessageBox.Show("Nie udało się uruchomić PhoneBridge.", "PhoneBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        return 1;
    }
}
