using System.Diagnostics;
using System.IO;
using PhoneBridge.Core.Config;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PhoneBridge.App;

/// <summary>Ikona w zasobniku systemowym z menu. Wywoływać tylko z wątku UI.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Agent _agent;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();

    public TrayIcon(Agent agent)
    {
        _agent = agent;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "PhoneBridge",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowStatus();
        };
        TrayMenuRenderer.Apply(_menu);
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
    }

    public void Refresh()
    {
        var text = "PhoneBridge – " + _agent.StatusText;
        // NotifyIcon.Text ma limit 127 znaków
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Notify(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(5000, title, text, icon);

    public void ShowStatus() => Notify("PhoneBridge", _agent.StatusText + Environment.NewLine + _agent.HowToPair);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new Forms.ToolStripMenuItem(_agent.StatusText)
        {
            Enabled = false,
            Font = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold),
        });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Wyślij pliki na telefon…", null, (_, _) => _agent.PickAndSend())
        {
            ShortcutKeyDisplayString = _agent.HotkeyEnabled ? _agent.Hotkey.ToString() : null,
        });
        _menu.Items.Add("Otwórz folder odbioru", null, (_, _) => OpenFolder(_agent.ReceiveFolder));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var devices = new Forms.ToolStripMenuItem("Sparowane telefony");
        var paired = _agent.PairedDevices;
        if (paired.Count == 0)
            devices.DropDownItems.Add(new Forms.ToolStripMenuItem("(brak)") { Enabled = false });
        foreach (var device in paired)
            devices.DropDownItems.Add($"Usuń „{device.Name}”…", null, (_, _) => _agent.Forget(device));
        _menu.Items.Add(devices);
        _menu.Items.Add("Jak sparować telefon?", null, (_, _) => Forms.MessageBox.Show(_agent.HowToPair, "PhoneBridge"));

        _menu.Items.Add(new Forms.ToolStripMenuItem("Odbieraj bez pytania (autoakceptacja)", null, (_, _) => _agent.ToggleAutoAccept())
        {
            Checked = _agent.AutoAccept,
        });
        var hotkey = new Forms.ToolStripMenuItem($"Skrót klawiaturowy ({_agent.Hotkey})");
        hotkey.DropDownItems.Add(new Forms.ToolStripMenuItem("Włączony", null, (_, _) => _agent.ToggleHotkey())
        {
            Checked = _agent.HotkeyEnabled,
        });
        hotkey.DropDownItems.Add("Zmień skrót…", null, (_, _) => _agent.ChangeHotkey());
        _menu.Items.Add(hotkey);
        _menu.Items.Add(new Forms.ToolStripMenuItem("Uruchamiaj przy logowaniu", null, (_, _) => _agent.ToggleAutostart())
        {
            Checked = ShellIntegration.IsAutostartEnabled(),
        });
        _menu.Items.Add("Otwórz dziennik diagnostyczny", null, (_, _) => OpenFolder(ConfigStore.DefaultDirectory));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Zakończ", null, (_, _) => _agent.Exit());
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
