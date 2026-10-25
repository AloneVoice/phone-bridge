using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneBridge.Core.Security;

public static class Fingerprint
{
    /// <summary>SHA-256 z DER certyfikatu, 64 znaki hex (małe litery).</summary>
    public static string Of(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

    public static bool IsValidHex(string? value, int bytes = 32) =>
        value is not null && value.Length == bytes * 2 && value.All(Uri.IsHexDigit);
}

public static class CertificateFactory
{
    /// <summary>Tworzy samopodpisany certyfikat ECDSA P-256 i zwraca go jako PFX (bez hasła).</summary>
    public static byte[] CreatePfx(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(commonName);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        return cert.Export(X509ContentType.Pfx);
    }

    public static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}

/// <summary>Krótki kod porównywany przez użytkownika podczas parowania (patrz docs/protocol.md).</summary>
public static class Sas
{
    private static readonly byte[] Label = "PhoneBridge-SAS-v1"u8.ToArray();

    public static string Compute(string serverFingerprint, string clientFingerprint, byte[] phoneNonce, byte[] serverNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Label);
        hash.AppendData(Convert.FromHexString(serverFingerprint));
        hash.AppendData(Convert.FromHexString(clientFingerprint));
        hash.AppendData(phoneNonce);
        hash.AppendData(serverNonce);
        var digest = hash.GetHashAndReset();
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        return value.ToString("D6");
    }

    public static string Format(string code) => $"{code[..3]} {code[3..]}";
}
