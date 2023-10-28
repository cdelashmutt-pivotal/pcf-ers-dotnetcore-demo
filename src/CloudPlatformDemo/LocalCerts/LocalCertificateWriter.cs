﻿// forked from steeltoe until this is included in official release: https://github.com/SteeltoeOSS/Steeltoe/pull/869

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloudPlatformDemo.LocalCerts;

public class LocalCertificateWriter
{
    internal string CertificateFilenamePrefix { get; set; } = "SteeltoeInstance";

    public static readonly string AppBasePath = AppContext.BaseDirectory.Substring(0, AppContext.BaseDirectory.LastIndexOf(Path.DirectorySeparatorChar + "bin"));

    public string RootCAPfxPath { get; set; } = Path.Combine(Directory.GetParent(AppBasePath).ToString(), "GeneratedCertificates", "SteeltoeCA.pfx");

    public string IntermediatePfxPath { get; set; } = Path.Combine(Directory.GetParent(AppBasePath).ToString(), "GeneratedCertificates", "SteeltoeIntermediate.pfx");

    public bool Write(Guid orgId, Guid spaceId)
    {
        var appId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        // Certificates provided by Diego will have a subject that doesn't comply with standards, but CertificateRequest would re-order these components anyway
        // Diego subjects will look like this: "CN=<instanceId>, OU=organization:<organizationId> + OU=space:<spaceId> + OU=app:<appId>"
        var subject = $"CN={instanceId}, OU=app:{appId} + OU=space:{spaceId} + OU=organization:{orgId}";

        X509Certificate2 caCertificate;

        // Create Root CA and intermediate cert PFX with private key (if not already there)
        if (!Directory.Exists(Path.Combine(Directory.GetParent(AppBasePath).ToString(), "GeneratedCertificates")))
        {
            Directory.CreateDirectory(Path.Combine(Directory.GetParent(AppBasePath).ToString(), "GeneratedCertificates"));
        }

        if (!File.Exists(RootCAPfxPath))
        {
            caCertificate = CreateRoot("CN=SteeltoeGeneratedCA");
            File.WriteAllBytes(RootCAPfxPath, caCertificate.Export(X509ContentType.Pfx));
        }
        else
        {
            caCertificate = new X509Certificate2(RootCAPfxPath);
        }

        X509Certificate2 intermediateCertificate;

        // Create intermediate cert PFX with private key (if not already there)
        if (!File.Exists(IntermediatePfxPath))
        {
            intermediateCertificate = CreateIntermediate("CN=SteeltoeGeneratedIntermediate", caCertificate);
            File.WriteAllBytes(IntermediatePfxPath, intermediateCertificate.Export(X509ContentType.Pfx));
        }
        else
        {
            intermediateCertificate = new X509Certificate2(IntermediatePfxPath);
        }

        var clientCertificate = CreateClient(subject, intermediateCertificate, new SubjectAlternativeNameBuilder());

        // Create a folder inside the project to store generated certificate files
        if (!Directory.Exists(Path.Combine(AppBasePath, "GeneratedCertificates")))
        {
            Directory.CreateDirectory(Path.Combine(AppBasePath, "GeneratedCertificates"));
        }

        var certContents =
            "-----BEGIN CERTIFICATE-----\r\n" +
            Convert.ToBase64String(clientCertificate.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks) +
            "\r\n-----END CERTIFICATE-----\r\n" +
            "-----BEGIN CERTIFICATE-----\r\n" +
            Convert.ToBase64String(intermediateCertificate.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks) +
            "\r\n-----END CERTIFICATE-----\r\n";

        File.WriteAllText(Path.Combine(AppBasePath, "GeneratedCertificates", CertificateFilenamePrefix + "Cert.pem"), certContents);
        File.WriteAllText(Path.Combine(AppBasePath, "GeneratedCertificates", CertificateFilenamePrefix + "Key.pem"), "-----BEGIN RSA PRIVATE KEY-----\r\n" + Convert.ToBase64String(clientCertificate.GetRSAPrivateKey().ExportRSAPrivateKey(), Base64FormattingOptions.InsertLineBreaks) + "\r\n-----END RSA PRIVATE KEY-----");

        return true;
    }

    private static X509Certificate2 CreateRoot(string name)
    {
        using var key = RSA.Create();
        var request = new CertificateRequest(new X500DistinguishedName(name), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow, new DateTimeOffset(2039, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    private static X509Certificate2 CreateIntermediate(string name, X509Certificate2 issuer)
    {
        using var key = RSA.Create();
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        var serialNumber = new byte[8];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(serialNumber);

        return request.Create(issuer, DateTimeOffset.UtcNow, issuer.NotAfter, serialNumber).CopyWithPrivateKey(key);
    }

    private static X509Certificate2 CreateClient(string name, X509Certificate2 issuer, SubjectAlternativeNameBuilder altNames, DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create();
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            Oid.FromFriendlyName("Server Authentication", OidGroup.EnhancedKeyUsage),
            Oid.FromFriendlyName("Client Authentication", OidGroup.EnhancedKeyUsage)
        }, false));

        if (altNames != null)
        {
            request.CertificateExtensions.Add(altNames.Build());
        }

        var serialNumber = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(serialNumber);
        }

        var signedCert = request.Create(issuer, DateTimeOffset.UtcNow, notAfter ?? DateTimeOffset.UtcNow.AddDays(1), serialNumber);

        return signedCert.CopyWithPrivateKey(key);
    }
}