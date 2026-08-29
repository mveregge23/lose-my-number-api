// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dbr.Api.InternalEdge;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dbr.Api.Tests.InternalEdge;

/// <summary>
/// The branch holds even when the pipeline is put together the other way.
/// </summary>
/// <remarks>
/// <para>
/// The composition root calls <c>UseRouting</c> explicitly after the branch, so an internal
/// request never reaches the public route table at all. That ordering is easy to lose: delete
/// the one line and the host inserts routing at the very front instead, where it matches a
/// public route <i>before</i> the branch is entered — and the branch then has to undo the
/// match rather than simply not make it.
/// </para>
/// <para>
/// This builds the application in exactly that shape, without the explicit call, and asserts
/// the internal listener still answers nothing public. It is the only thing that exercises the
/// branch clearing an inherited endpoint: found by mutation, when deleting that clear broke no
/// test at all because every other test here is arranged so nothing is ever inherited.
/// </para>
/// </remarks>
public class InternalEdgeBranchOrderingTests : IAsyncLifetime
{
    private const string WorkerName = "dbr-worker";

    private readonly TestPki _pki = TestPki.Create();

    private WebApplication _app = null!;

    private int _publicPort;

    private int _internalPort;

    public async ValueTask InitializeAsync()
    {
        _publicPort = FreePort();
        _internalPort = FreePort();

        var directory = Path.Combine(Path.GetTempPath(), $"dbr-order-{Guid.NewGuid():N}");
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

        builder.Services.AddSingleton<IIdentityReleaseService>(new NeverGrants());

        builder.AddDbrInternalEdge();

        _app = builder.Build();

        _app.UseDbrInternalEdge();

        // Deliberately no UseRouting here. That is what makes the host put routing at the
        // front, ahead of the branch, which is the arrangement being tested.
        _app.MapGet("/ping", () => "pong");

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        _pki.Dispose();

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_public_route_matched_before_the_branch_does_not_run_inside_it()
    {
        using var client = ClientWith(_pki.Issue(WorkerName, forServer: false));

        var response = await client.GetAsync(
            $"https://localhost:{_internalPort}/ping",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_internal_route_still_answers_on_its_own_listener()
    {
        // The other half: clearing the endpoint must not also clear the branch's own match.
        using var client = ClientWith(_pki.Issue(WorkerName, forServer: false));

        var response = await client.PostAsync(
            $"https://localhost:{_internalPort}/internal/v1/vault/release",
            new StringContent("{\"token\":\"anything\"}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Forbidden rather than NotFound: the route was found and the grant was refused.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private HttpClient ClientWith(X509Certificate2 certificate) =>
        new(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [certificate],
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { _pki.Authority },
                    RevocationMode = X509RevocationMode.NoCheck,
                },
            },
        });

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>Refuses every grant, because these tests are about which route answers.</summary>
    private sealed class NeverGrants : IIdentityReleaseService
    {
        public Task<MintReleaseResult> MintAsync(
            Guid scanId,
            Guid brokerId,
            IReadOnlyCollection<IdentityField> fields,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The edge does not mint.");

        public Task<RedeemReleaseResult> RedeemAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(RedeemReleaseResult.Refused());
    }
}
