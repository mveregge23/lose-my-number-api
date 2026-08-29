// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dbr.Api.Tests.InternalEdge;

/// <summary>
/// A throwaway certificate authority and the certificates it issues, for tests.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than checked in. A committed certificate is a private key in the
/// repository, and one with a fixed expiry is also a test that starts failing on a date
/// nobody chose — both worse than a few milliseconds of RSA per run.
/// </para>
/// <para>
/// The PEM text is kept from the moment of generation rather than exported back out of the
/// finished certificate. A key loaded from PKCS#12 is not necessarily marked exportable —
/// on macOS it is not — so asking the certificate for its key later throws, and the fix of
/// demanding an exportable key would make the test material less like the real thing
/// rather than more.
/// </para>
/// </remarks>
internal sealed class TestPki : IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    private readonly List<X509Certificate2> _owned = [];

    private TestPki(X509Certificate2 authority) => Authority = authority;

    /// <summary>The authority, certificate only — what a trust decision is made against.</summary>
    public X509Certificate2 Authority { get; }

    public static TestPki Create(string authorityName = "dbr-test-ca")
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={authorityName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));

        var authority = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        var pki = new TestPki(authority);
        pki._owned.Add(authority);

        return pki;
    }

    /// <summary>A certificate this authority issued, with its key, usable for TLS.</summary>
    public X509Certificate2 Issue(string commonName, bool forServer) =>
        IssuePair(commonName, forServer).Certificate;

    /// <summary>The same, keeping the PEM text so it can be written to disk.</summary>
    public TestCertificate IssuePair(string commonName, bool forServer)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(forServer ? ServerAuth : ClientAuth)], false));

        if (forServer)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        using var issued = request.Create(
            Authority,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(29),
            RandomNumberGenerator.GetBytes(16));

        using var withKey = issued.CopyWithPrivateKey(key);

        // Round-tripped through PKCS#12 for the same reason production does it: a
        // certificate assembled in memory carries its key in a form some platforms decline
        // to use for a handshake.
        var usable = X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12),
            password: null);

        _owned.Add(usable);

        return new TestCertificate(usable, issued.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Writes a certificate, and its key when there is one, as PEM files.</summary>
    /// <remarks>
    /// The settings name paths rather than certificates, so anything testing them has to
    /// produce real files — which also covers the loading, and the loading is where a
    /// private key quietly fails to come along.
    /// </remarks>
    public static void Write(string directory, string name, string certificatePem, string? keyPem = null)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, $"{name}.crt"), certificatePem);

        if (keyPem is not null)
        {
            File.WriteAllText(Path.Combine(directory, $"{name}.key"), keyPem);
        }
    }

    public void Dispose()
    {
        foreach (var certificate in _owned)
        {
            certificate.Dispose();
        }

        _owned.Clear();
    }
}

/// <summary>A generated certificate, and the PEM text it came from.</summary>
internal sealed record TestCertificate(
    X509Certificate2 Certificate,
    string CertificatePem,
    string KeyPem);
