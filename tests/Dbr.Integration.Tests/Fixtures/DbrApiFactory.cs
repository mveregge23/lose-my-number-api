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
internal sealed class DbrApiFactory(string connectionString) : WebApplicationFactory<Program>
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
        builder.UseSetting("Tokens:SigningKey", PostgresFixture.TestSigningKey);
        builder.UseSetting("Passkeys:RelyingPartyId", "localhost");
        builder.UseSetting("Passkeys:Origins:0", Origin);
    }
}
