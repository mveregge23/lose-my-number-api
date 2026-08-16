// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

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
    string? baoToken = null)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// Where the browser is pretended to be. The relying party is the bare host of
    /// this, which is what <c>PasskeyOptions</c> requires and what the test
    /// authenticator hashes into its authenticator data.
    /// </summary>
    public const string Origin = "https://localhost";

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
    }
}
