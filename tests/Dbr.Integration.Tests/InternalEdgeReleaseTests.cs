// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The release route, with the real service behind it.
/// </summary>
/// <remarks>
/// <para>
/// What is new here is the wiring: that the route the workers call resolves the same
/// release service the composition root registers, over the same database, vault and key
/// manager everything else uses. The listener's own behaviour — the handshake, who is
/// turned away, which listener answers what — is settled against real sockets elsewhere,
/// because an in-process host has no handshake to make.
/// </para>
/// <para>
/// Requests are dispatched with the connection's local port set, which is the fact the
/// branch reads. Nothing is bound, so this is not pretending to be a socket: it is stating
/// the one thing about a connection the branch decides on, and asserting the branch decides
/// the same way for both values.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class InternalEdgeReleaseTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ReleasePath = "/internal/v1/vault/release";

    private const string ProfilePath = "/api/v1/profile";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _brokerId;

    private string BrokerDomain => $"edge-broker-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(
            postgres.ConnectionString,
            openBao.Address,
            openBao.Token,
            internalEdge: true);

        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Edge Broker {_suffix}', '{BrokerDomain}', 'webform', 45, true);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.identity_release;
             DELETE FROM public.scan_broker;
             DELETE FROM public.scan;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             """);
    }

    /// <summary>
    /// A grant minted by the service, spent over the route a worker calls.
    /// </summary>
    /// <remarks>
    /// The whole path in one test: the identity is encrypted in the vault under a key the
    /// key manager holds, and comes back through the edge as the one group the grant named.
    /// </remarks>
    [Fact]
    public async Task A_grant_minted_here_can_be_spent_over_the_edge()
    {
        var token = await MintAsync(IdentityField.Names);

        var (status, body) = await PostAsync(DbrApiFactory.InternalPort, token);

        Assert.Equal(HttpStatusCode.OK, status);

        var released = JsonSerializer.Deserialize<ReleaseResponse>(body, Wire)!;

        Assert.Equal(["Alex Whitfield"], released.Names);
        Assert.Equal(["names"], released.Fields);

        // The groups nobody asked for are not in the answer, which is the property the
        // grant exists to carry and the one that has to survive the trip through JSON.
        Assert.Empty(released.Addresses);
        Assert.Empty(released.Contacts);
        Assert.Null(released.DateOfBirth);
    }

    [Fact]
    public async Task A_grant_is_spent_by_being_used()
    {
        var token = await MintAsync(IdentityField.Names);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(DbrApiFactory.InternalPort, token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(DbrApiFactory.InternalPort, token)).Status);
    }

    [Fact]
    public async Task A_token_nobody_minted_is_refused()
    {
        var (status, _) = await PostAsync(DbrApiFactory.InternalPort, "not-a-grant-anybody-issued");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// The same request, arriving on the public listener, finds nothing.
    /// </summary>
    /// <remarks>
    /// A valid grant and a well-formed body, refused by there being no such route rather
    /// than by anything about the request. That is the difference between an edge that is
    /// closed and one that is merely guarded.
    /// </remarks>
    [Fact]
    public async Task The_route_is_not_there_on_the_public_listener()
    {
        var token = await MintAsync(IdentityField.Names);

        var (status, _) = await PostAsync(DbrApiFactory.PublicPort, token);

        Assert.Equal(HttpStatusCode.NotFound, status);

        // And the grant is still good, because nothing on the public edge could reach the
        // service to spend it.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(DbrApiFactory.InternalPort, token)).Status);
    }

    private static JsonSerializerOptions Wire => new(JsonSerializerDefaults.Web);

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(int localPort, string token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { token }, Wire);
        var received = new MemoryStream();

        var context = await _factory.Server.SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Post;
            request.Request.Scheme = "https";
            request.Request.Host = new HostString("api", localPort);
            request.Request.Path = ReleasePath;
            request.Request.ContentType = "application/json";
            request.Request.ContentLength = payload.Length;
            request.Request.Body = new MemoryStream(payload);

            // A hand-built context reports that it cannot have a body, and minimal APIs
            // ask before they read — so without this the endpoint is handed nothing and
            // complains that nothing was sent.
            request.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyIsThere());

            // The one fact the branch reads, and the reason it is a fact rather than a
            // header: a caller chooses its headers and does not choose which socket its
            // connection lands on.
            request.Connection.LocalPort = localPort;

            // Held onto rather than read back off the context afterwards, which hands
            // back a stream that is finished with and refuses to be rewound.
            request.Response.Body = received;
        });

        return ((HttpStatusCode)context.Response.StatusCode, Encoding.UTF8.GetString(received.ToArray()));
    }

    /// <summary>Says a request has a body, for a context that was not read off a socket.</summary>
    private sealed class BodyIsThere : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    /// <summary>Opens an account with an identity, queues a scan, and mints a grant for it.</summary>
    private async Task<string> MintAsync(params IdentityField[] fields)
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"edge-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);
        var tenantId = ApiClient.TenantId(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        await _api.PutAsync(
            ProfilePath,
            new
            {
                names = new[] { "Alex Whitfield" },
                dateOfBirth = "1985-04-17",
                contacts = new[] { new { kind = "email", value = "alex@example.test" } },
            },
            token);

        var (_, scan) = await _api.PostAsync(ScansPath, new { }, token);
        var scanId = scan.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        var minted = await scope.ServiceProvider
            .GetRequiredService<IIdentityReleaseService>()
            .MintAsync(scanId, _brokerId, fields, TestContext.Current.CancellationToken);

        Assert.Equal(MintReleaseOutcome.Minted, minted.Outcome);

        return minted.Release!.Token;
    }
}
