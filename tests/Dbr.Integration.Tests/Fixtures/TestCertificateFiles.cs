// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// Certificate files good enough to start the internal listener.
/// </summary>
/// <remarks>
/// Deliberately not a certificate authority with issued leaves — the tests here drive the
/// internal routes through the in-process host, which has no handshake to make, so nothing
/// is ever verified against these. What they have to survive is being loaded, and loading
/// is where a private key quietly fails to come along with its certificate.
/// <para>
/// What a real handshake does with a real authority is covered against a real socket
/// elsewhere. Reproducing it here would make a test about the release service fail for
/// reasons about TLS.
/// </para>
/// </remarks>
internal static class TestCertificateFiles
{
    /// <summary>Writes a certificate and key, and returns the directory holding them.</summary>
    public static string Write()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dbr-edge-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=api-internal",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var pem = certificate.ExportCertificatePem();

        File.WriteAllText(Path.Combine(directory, "server.crt"), pem);
        File.WriteAllText(Path.Combine(directory, "server.key"), key.ExportPkcs8PrivateKeyPem());

        // Stands in for the authority file. Nothing chains to it in these tests; it only
        // has to parse, because the listener builds its gate at startup either way.
        File.WriteAllText(Path.Combine(directory, "ca.crt"), pem);

        return directory;
    }
}
