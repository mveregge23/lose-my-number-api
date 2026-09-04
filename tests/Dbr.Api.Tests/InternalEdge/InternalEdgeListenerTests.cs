// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dbr.Api.InternalEdge;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.InternalEdge;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dbr.Api.Tests.InternalEdge;

/// <summary>
/// The two listeners, over real sockets and a real handshake.
/// </summary>
/// <remarks>
/// <para>
/// Everything here needs a genuine TLS connection to mean anything. A test host that
/// dispatches requests in memory has no handshake to refuse, so it could not tell the
/// difference between a listener that demands a certificate and one that ignores the
/// setting entirely — which is the failure most worth catching, because it looks
/// identical from the inside.
/// </para>
/// <para>
/// The other half is disjointness. The internal routes are absent from the public
/// listener and the public routes are absent from the internal one, and both directions
/// are asserted: a route that merely refused would advertise its own existence, and one
/// that answered on both would make the port meaningless.
/// </para>
/// </remarks>
public class InternalEdgeListenerTests : IAsyncLifetime
{
    private const string WorkerName = "dbr-worker";

    private const string GoodToken = "a-grant-that-was-minted";

    private readonly TestPki _pki = TestPki.Create();

    private readonly TestPki _elsewhere = TestPki.Create("somebody-elses-ca");

    private WebApplication _app = null!;

    private int _publicPort;

    private int _internalPort;

    public async ValueTask InitializeAsync()
    {
        _publicPort = FreePort();
        _internalPort = FreePort();

        var directory = Path.Combine(Path.GetTempPath(), $"dbr-edge-{Guid.NewGuid():N}");
        var server = _pki.IssuePair("api-internal", forServer: true);

        TestPki.Write(directory, "ca", _pki.Authority.ExportCertificatePem());
        TestPki.Write(directory, "server", server.CertificatePem, server.KeyPem);

        var builder = WebApplication.CreateBuilder();

        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalEdge:Enabled"] = "true",
            ["InternalEdge:Port"] = _internalPort.ToString(),
            ["InternalEdge:PublicPort"] = _publicPort.ToString(),
            ["InternalEdge:ServerCertificatePath"] = Path.Combine(directory, "server.crt"),
            ["InternalEdge:ServerKeyPath"] = Path.Combine(directory, "server.key"),
            ["InternalEdge:ClientCertificateAuthorityPath"] = Path.Combine(directory, "ca.crt"),
            ["InternalEdge:ClientCertificateCommonName"] = WorkerName,
        });

        builder.Services.AddSingleton<IIdentityReleaseService>(new StubReleases());
        builder.Services.AddSingleton<IFindingReporter>(new StubReporter());

        builder.AddDbrInternalEdge();

        _app = builder.Build();

        _app.UseDbrInternalEdge();

        // Explicit, and after the branch. Without it the host inserts routing at the very
        // front of the pipeline, which matches a public route before the internal branch
        // is even entered — the same arrangement the composition root uses, so that this
        // tests the ordering the API actually runs.
        _app.UseRouting();

        // Stands in for every public route. What matters is only that the public table has
        // something in it, so "the internal listener answers nothing" is a statement about
        // the branch rather than about an empty application.
        _app.MapGet("/ping", () => "pong");

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        _pki.Dispose();
        _elsewhere.Dispose();

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_worker_can_spend_a_grant()
    {
        using var client = ClientWith(_pki.Issue(WorkerName, forServer: false));

        var response = await client.PostAsJsonAsync(
            InternalUri("/internal/v1/vault/release"),
            new ReleaseRequest(GoodToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var released = await response.Content.ReadFromJsonAsync<ReleaseResponse>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(released);
        Assert.Equal(["Alex Whitfield"], released.Names);
        Assert.Equal(["names"], released.Fields);
    }

    [Fact]
    public async Task A_grant_that_cannot_be_spent_is_refused_without_saying_why()
    {
        using var client = ClientWith(_pki.Issue(WorkerName, forServer: false));

        var response = await client.PostAsJsonAsync(
            InternalUri("/internal/v1/vault/release"),
            new ReleaseRequest("not-a-grant"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// No certificate, no conversation.
    /// </summary>
    /// <remarks>
    /// The refusal happens during the handshake, so there is no status code to inspect —
    /// which is the point. A caller that cannot prove which machine it is never gets to
    /// send a request line, let alone reach a route.
    /// </remarks>
    [Fact]
    public async Task A_caller_with_no_certificate_never_gets_a_response()
    {
        using var client = ClientWith(certificate: null);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetAsync(InternalUri("/internal/v1/vault/release"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_certificate_from_another_authority_never_gets_a_response()
    {
        using var client = ClientWith(_elsewhere.Issue(WorkerName, forServer: false));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetAsync(InternalUri("/internal/v1/vault/release"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_right_authority_with_the_wrong_name_never_gets_a_response()
    {
        using var client = ClientWith(_pki.Issue("some-other-service", forServer: false));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetAsync(InternalUri("/internal/v1/vault/release"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The property the whole arrangement exists for.
    /// </summary>
    /// <remarks>
    /// A plain 404, the same answer a path nobody ever wrote would get. Not 401, not 403 —
    /// either of those would confirm the route is there, and a route that exists and
    /// refuses is one misconfiguration away from a route that exists and answers.
    /// </remarks>
    [Fact]
    public async Task The_internal_route_is_absent_from_the_public_listener()
    {
        using var client = new HttpClient();

        var response = await client.PostAsJsonAsync(
            PublicUri("/internal/v1/vault/release"),
            new ReleaseRequest(GoodToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_path_nobody_wrote_gets_the_same_answer_on_the_public_listener()
    {
        // The comparison that gives the test above its meaning: if the internal path
        // answered differently from a made-up one, it would be discoverable.
        using var client = new HttpClient();

        var madeUp = await client.GetAsync(
            PublicUri("/nothing/here"),
            TestContext.Current.CancellationToken);

        var internalPath = await client.GetAsync(
            PublicUri("/internal/v1/vault/release"),
            TestContext.Current.CancellationToken);

        Assert.Equal(madeUp.StatusCode, internalPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, internalPath.StatusCode);
    }

    [Fact]
    public async Task The_public_listener_still_serves_public_routes()
    {
        // Enabling the internal listener must not take the public one over. Kestrel
        // prefers endpoints configured in code to the addresses the host was launched
        // with, so this is the assertion that the second one was configured too.
        using var client = new HttpClient();

        var response = await client.GetAsync(PublicUri("/ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Public_routes_are_absent_from_the_internal_listener()
    {
        // The other direction, so the two edges are disjoint rather than nested. A worker
        // holding a certificate is not thereby a client of the ordinary API.
        using var client = ClientWith(_pki.Issue(WorkerName, forServer: false));

        var response = await client.GetAsync(InternalUri("/ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private string PublicUri(string path) => $"http://localhost:{_publicPort}{path}";

    private string InternalUri(string path) => $"https://localhost:{_internalPort}{path}";

    private HttpClient ClientWith(X509Certificate2? certificate)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { _pki.Authority },
                RevocationMode = X509RevocationMode.NoCheck,
            },
        };

        if (certificate is not null)
        {
            ssl.ClientCertificates = [certificate];
        }

        return new HttpClient(new SocketsHttpHandler { SslOptions = ssl });
    }

    /// <summary>A port nothing is listening on, as far as the operating system knows.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>
    /// Stands in for the release service, so these tests are about the edge.
    /// </summary>
    /// <remarks>
    /// What a grant means, and whether this one may be spent, is settled and tested
    /// elsewhere against a real database and a real key manager. Repeating it here would
    /// make a socket test fail for reasons that have nothing to do with sockets.
    /// </remarks>
    private sealed class StubReleases : IIdentityReleaseService
    {
        public Task<MintReleaseResult> MintAsync(
            Guid scanId,
            Guid brokerId,
            IReadOnlyCollection<IdentityField> fields,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The edge does not mint.");

        public Task<MintReleaseResult> MintForJobAsync(
            Guid removalJobId,
            Guid brokerId,
            IReadOnlyCollection<IdentityField> fields,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The edge does not mint.");

        public Task<RedeemReleaseResult> RedeemAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(token == GoodToken
                ? RedeemReleaseResult.Granted(new RedeemedRelease(
                    Guid.NewGuid(),
                    RemovalJobId: null,
                    Guid.NewGuid(),
                    [IdentityField.Names],
                    new ProfileIdentityFields(["Alex Whitfield"], [], [], null)))
                : RedeemReleaseResult.Refused());
    }

    /// <summary>
    /// The other route on this listener, stood up so the branch has both to map.
    /// </summary>
    /// <remarks>
    /// These tests are about the listener — which routes it answers, which it has never heard
    /// of — so what the routes do behind them is somebody else's test. It has to be registered
    /// all the same: a route whose handler cannot be built fails the whole pipeline, and the
    /// symptom is every one of these returning 500 rather than the route being missing.
    /// </remarks>
    private sealed class StubReporter : IFindingReporter
    {
        public Task<ReportFindingsResult> ReportAsync(
            string token,
            IReadOnlyList<ReportedListing> listings,
            CancellationToken cancellationToken) =>
            Task.FromResult(token == GoodToken
                ? new ReportFindingsResult(ReportFindingsOutcome.Recorded, listings.Count, 0)
                : ReportFindingsResult.Refused());
    }
}
