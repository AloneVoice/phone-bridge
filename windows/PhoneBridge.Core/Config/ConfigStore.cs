using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PhoneBridge.Core.Protocol;
using PhoneBridge.Core.Security;

namespace PhoneBridge.Core.Config;

public sealed class TrustedDevice
{
    public required string Fingerprint { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset PairedAt { get; init; }
}

public sealed class AppConfig
{
    /// <summary>PFX certyfikatu serwera zaszyfrowany przez <see cref="ISecretProtector"/>, Base64.</summary>
    public string? ServerCertificate { get; set; }
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    public List<TrustedDevice> Devices { get; set; } = [];
    public string? DefaultFolder { get; set; }
    public bool AutoAccept { get; set; }
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Modyfikatory RegisterHotKey (Alt=1, Ctrl=2, Shift=4, Win=8). Domyślnie Ctrl+Alt+P.</summary>
    public uint HotkeyModifiers { get; set; } = 0x3;

    /// <summary>Kod klawisza (virtual-key).</summary>
    public uint HotkeyKey { get; set; } = 0x50;
}

public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "PhoneBridge.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Konfiguracja w %APPDATA%\PhoneBridge\config.json. Bezpieczna wątkowo.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions FileOptions = new(Json.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _sync = new();
    private readonly AppConfig _config;

    public ConfigStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _config = TryLoad(path) ?? new AppConfig();
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneBridge");

    public event Action? DevicesChanged;

    public int Port { get { lock (_sync) return _config.Port; } }
    public bool AutoAccept { get { lock (_sync) return _config.AutoAccept; } }
    public string? DefaultFolder { get { lock (_sync) return _config.DefaultFolder; } }
    public bool HotkeyEnabled { get { lock (_sync) return _config.HotkeyEnabled; } }
    public (uint Modifiers, uint Key) Hotkey { get { lock (_sync) return (_config.HotkeyModifiers, _config.HotkeyKey); } }

    public IReadOnlyList<TrustedDevice> Devices
    {
        get { lock (_sync) return _config.Devices.ToList(); }
    }

    public TrustedDevice? FindDevice(string fingerprint)
    {
        lock (_sync)
            return _config.Devices.FirstOrDefault(d => d.Fingerprint == fingerprint);
    }

    public TrustedDevice AddDevice(string fingerprint, string name)
    {
        var device = new TrustedDevice { Fingerprint = fingerprint, Name = name, PairedAt = DateTimeOffset.UtcNow };
        lock (_sync)
        {
            _config.Devices.RemoveAll(d => d.Fingerprint == fingerprint);
            _config.Devices.Add(device);
            Save();
        }
        DevicesChanged?.Invoke();
        return device;
    }

    public void RemoveDevice(string fingerprint)
    {
        lock (_sync)
        {
            _config.Devices.RemoveAll(d => d.Fingerprint == fingerprint);
            Save();
        }
        DevicesChanged?.Invoke();
    }

    public void Update(Action<AppConfig> change)
    {
        lock (_sync)
        {
            change(_config);
            Save();
        }
    }

    public X509Certificate2 GetOrCreateServerCertificate(string commonName)
    {
        lock (_sync)
        {
            if (_config.ServerCertificate is { } stored)
            {
                try
                {
                    return CertificateFactory.Load(_protector.Unprotect(Convert.FromBase64String(stored)));
                }
                catch (Exception e) when (e is CryptographicException or FormatException)
                {
                    // np. konfiguracja skopiowana z innego konta – tworzymy nową tożsamość, parowania tracą ważność
                    Log.Error("Nie można odczytać certyfikatu serwera, tworzę nowy", e);
                    _config.Devices.Clear();
                }
            }

            var pfx = CertificateFactory.CreatePfx(commonName);
            _config.ServerCertificate = Convert.ToBase64String(_protector.Protect(pfx));
            Save();
            return CertificateFactory.Load(pfx);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_config, FileOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static AppConfig? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), FileOptions) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Error("Uszkodzony plik konfiguracji, używam domyślnej", e);
            return null;
        }
    }
}
