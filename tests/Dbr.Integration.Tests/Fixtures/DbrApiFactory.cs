// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// The real API, in-process, over a real database.
/// </summary>
/// <remarks>
/// <para>
/// This boots <c>Program</c> itself — the same composition root the container runs —
/// so what these tests exercise is the actual pipeline: routing, bearer
/// authentication, the middleware that turns a validated claim into the current
/// tenant, the interceptor that writes it onto every connection, and the policies
/// underneath. <b>Nothing is substituted.</b> A test host with a stubbed step answers
/// a question about a program nobody runs, and the steps most worth testing here are
/// exactly the ones a stub would replace.
/// </para>
/// <para>
/// Configuration is supplied the way a deployment supplies it, through settings the
/// composition root reads, rather than by reaching into the service collection
/// afterwards. That keeps the startup validation in the path: a test host that
/// bypassed it would boot happily on configuration the real thing refuses.
/// </para>
/// </remarks>
/// <param name="baoAddress">
/// A real key manager for the tests that store an identity, and nothing listening for
/// the ones that do not — see the note on the settings below.
/// </param>
internal sealed class DbrApiFactory(
    string connectionString,
    string? baoAddress = null,
    string? baoToken = null,
    bool internalEdge = false)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// The port the internal branch answers on, when this factory turns it on.
    /// </summary>
    /// <remarks>
    /// The in-process host binds nothing, so this is not a socket — it is the value a test
    /// puts on a request's connection to say which listener it should be treated as having
    /// arrived at, and the number the branch compares against.
    /// </remarks>
    public const int InternalPort = 18443;

    public const int PublicPort = 18080;

    /// <summary>
    /// Where the browser is pretended to be. The relying party is the bare host of
    /// this, which is what <c>PasskeyOptions</c> requires and what the test
    /// authenticator hashes into its authenticator data.
    /// </summary>
    public const string Origin = "https://localhost";

    /// <summary>
    /// The terms this test instance serves. Signup refuses any other version, so a test
    /// that accepts something else is testing the refusal.
    /// </summary>
    public const string TermsVersion = "2026-06-01";

    /// <summary>
    /// The consent text this test instance serves. Deliberately a different value from
    /// <see cref="TermsVersion"/>: they are separate documents on separate clocks, and
    /// two settings that happen to hold the same string would let a test pass while the
    /// code read the wrong one.
    /// </summary>
    public const string ConsentPolicyVersion = "consent-2026-07-15";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:Core", connectionString);

        // The same database, reached as the vault role — which is how compose supplies
        // it too, and how the API distinguishes the two stores while they share one.
        builder.UseSetting("ConnectionStrings:Vault", connectionString);
        builder.UseSetting("Tokens:SigningKey", PostgresFixture.TestSigningKey);
        builder.UseSetting("Passkeys:RelyingPartyId", "localhost");
        builder.UseSetting("Passkeys:Origins:0", Origin);
        builder.UseSetting("Terms:CurrentVersion", TermsVersion);
        builder.UseSetting("Consent:PolicyVersion", ConsentPolicyVersion);

        // Without an address supplied, a placeholder that satisfies startup validation
        // and nothing more: those tests exercise the request pipeline, not encryption,
        // and pointing at a port with nothing behind it makes an unexpected call to the
        // key manager a loud failure rather than a quiet one.
        //
        // Supplying it at all is the point: the composition root refuses to start
        // without it, and a test host that skipped that check would boot happily on
        // configuration the real thing rejects.
        builder.UseSetting("Bao:Address", baoAddress ?? "http://127.0.0.1:1");
        builder.UseSetting("Bao:Token", baoToken ?? "not-a-usable-token");

        if (!internalEdge)
        {
            // Off unless a test asks for it, which is also what a deployment that has said
            // nothing gets — so every other test here is exercising the composition root
            // in the shape most of them run in.
            return;
        }

        var certificates = TestCertificateFiles.Write();

        builder.UseSetting("InternalEdge:Enabled", "true");
        builder.UseSetting("InternalEdge:Port", InternalPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("InternalEdge:PublicPort", PublicPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("InternalEdge:ServerCertificatePath", Path.Combine(certificates, "server.crt"));
        builder.UseSetting("InternalEdge:ServerKeyPath", Path.Combine(certificates, "server.key"));
        builder.UseSetting("InternalEdge:ClientCertificateAuthorityPath", Path.Combine(certificates, "ca.crt"));
        builder.UseSetting("InternalEdge:ClientCertificateCommonName", "dbr-worker");
    }
}
