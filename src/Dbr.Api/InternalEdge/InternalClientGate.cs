// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography.X509Certificates;

namespace Dbr.Api.InternalEdge;

/// <summary>
/// Decides whether a certificate presented at the internal listener belongs to this
/// deployment's worker.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and both have to answer yes. <b>Was this signed by the authority this
/// deployment named</b> — checked against that certificate alone rather than against the
/// machine's trust store, which holds every public authority the operating system ships
/// with and would therefore accept a certificate anybody could buy. And <b>is it the one
/// certificate that authority issued for the worker</b>, checked by common name, because an
/// authority that has issued one certificate will eventually have issued several.
/// </para>
/// <para>
/// This runs during the TLS handshake, before a single byte of HTTP is parsed. A caller
/// that cannot answer both questions never reaches routing, never reaches a handler, and
/// never gets to send a request line — which is a stronger position than any check
/// expressed as a middleware, because there is no pipeline for it to be misordered in.
/// </para>
/// <para>
/// It authenticates a machine and nothing more. What that machine may then <i>do</i> is
/// carried by the release token it presents, which is deliberately not the same question:
/// a certificate says which process is calling, and says nothing about whose identity it
/// should be allowed to open.
/// </para>
/// </remarks>
public sealed class InternalClientGate
{
    private readonly X509Certificate2 _authority;

    private readonly string _commonName;

    public InternalClientGate(X509Certificate2 authority, string commonName)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        _authority = authority;
        _commonName = commonName;
    }

    /// <summary>Whether this certificate may open a connection to the internal listener.</summary>
    public bool Accepts(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        // Ordered cheapest first, and the order is not load-bearing: both have to hold, so
        // neither can be reached around by a certificate that satisfies only the other.
        if (!HasCommonName(certificate))
        {
            return false;
        }

        return ChainsToAuthority(certificate);
    }

    private bool HasCommonName(X509Certificate2 certificate) =>
        string.Equals(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            _commonName,
            StringComparison.Ordinal);

    private bool ChainsToAuthority(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();

        // The named authority is the only root, rather than one root among the machine's.
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_authority);

        // No revocation check, and this is a real limitation rather than an oversight. A
        // private authority issuing a handful of certificates publishes no revocation list
        // to consult, so asking for one means every handshake waits on a lookup that cannot
        // succeed. A deployment that does publish one should turn this on; until it does,
        // withdrawing a worker's access means reissuing the authority.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(certificate);
    }
}
