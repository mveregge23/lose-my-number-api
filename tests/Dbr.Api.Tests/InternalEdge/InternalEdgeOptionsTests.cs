// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.InternalEdge;
using Microsoft.Extensions.Configuration;

namespace Dbr.Api.Tests.InternalEdge;

/// <summary>
/// What the internal listener refuses to start on.
/// </summary>
/// <remarks>
/// Every one of these fails at the moment a worker asks for somebody's identity if it is
/// not caught here — as a handshake that will not complete, which reads like a network
/// fault rather than like a missing file.
/// </remarks>
public class InternalEdgeOptionsTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"dbr-edge-options-{Guid.NewGuid():N}");

    public InternalEdgeOptionsTests()
    {
        var server = _pki.IssuePair("api-internal", forServer: true);

        TestPki.Write(_directory, "ca", _pki.Authority.ExportCertificatePem());
        TestPki.Write(_directory, "server", server.CertificatePem, server.KeyPem);
    }

    /// <summary>
    /// The default is off, and off is complete rather than half-configured.
    /// </summary>
    /// <remarks>
    /// A deployment that has said nothing about the internal edge gets no listener, no
    /// routes and no complaint. The alternative — refusing to start until certificates
    /// exist — would make every developer running the API produce a certificate authority
    /// before seeing a page.
    /// </remarks>
    [Fact]
    public void Saying_nothing_asks_for_nothing()
    {
        var options = new InternalEdgeOptions();

        options.Validate();

        Assert.False(options.Enabled);
    }

    [Fact]
    public void A_disabled_edge_does_not_have_to_be_configured()
    {
        var options = new InternalEdgeOptions { Enabled = false, ServerCertificatePath = "nowhere" };

        options.Validate();
    }

    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Complete().Validate();
    }

    [Theory]
    [InlineData(nameof(InternalEdgeOptions.ServerCertificatePath))]
    [InlineData(nameof(InternalEdgeOptions.ServerKeyPath))]
    [InlineData(nameof(InternalEdgeOptions.ClientCertificateAuthorityPath))]
    public void Every_certificate_path_is_required(string property)
    {
        var options = Complete();
        typeof(InternalEdgeOptions).GetProperty(property)!.SetValue(options, string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(property, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(InternalEdgeOptions.ServerCertificatePath))]
    [InlineData(nameof(InternalEdgeOptions.ServerKeyPath))]
    [InlineData(nameof(InternalEdgeOptions.ClientCertificateAuthorityPath))]
    public void A_certificate_path_that_is_not_there_is_refused(string property)
    {
        var options = Complete();
        typeof(InternalEdgeOptions).GetProperty(property)!
            .SetValue(options, Path.Combine(_directory, "absent.pem"));

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>
    /// The name is not optional, and the reason is the whole point of having it.
    /// </summary>
    [Fact]
    public void The_client_name_is_required()
    {
        var options = Complete();
        options.ClientCertificateCommonName = "   ";

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("every certificate", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One listener cannot be both edges.
    /// </summary>
    /// <remarks>
    /// The port is the entire mechanism separating the internal routes from the public
    /// ones. Configured the same, the branch would take every request and the worker-facing
    /// routes would be on the open edge — which is the one outcome this whole arrangement
    /// exists to prevent, arrived at by a typo.
    /// </remarks>
    [Fact]
    public void The_two_listeners_cannot_share_a_port()
    {
        var options = Complete();
        options.PublicPort = options.Port;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Something_that_is_not_a_port_is_refused(int port)
    {
        var options = Complete();
        options.Port = port;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>
    /// The settings as an operator writes them, bound the way the composition root binds.
    /// </summary>
    /// <remarks>
    /// Everything above constructs the options directly and would pass just as well if the
    /// section name were wrong or a boolean did not bind — and that failure is an internal
    /// edge that silently never comes up, which looks exactly like one that was never
    /// wanted.
    /// </remarks>
    [Fact]
    public void The_settings_bind_from_the_shape_compose_writes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalEdge:Enabled"] = "true",
                ["InternalEdge:Port"] = "8443",
                ["InternalEdge:PublicPort"] = "8080",
                ["InternalEdge:ServerCertificatePath"] = Path.Combine(_directory, "server.crt"),
                ["InternalEdge:ServerKeyPath"] = Path.Combine(_directory, "server.key"),
                ["InternalEdge:ClientCertificateAuthorityPath"] = Path.Combine(_directory, "ca.crt"),
                ["InternalEdge:ClientCertificateCommonName"] = "dbr-worker",
            })
            .Build();

        var options = new InternalEdgeOptions();
        configuration.GetSection(InternalEdgeOptions.SectionName).Bind(options);

        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal(8443, options.Port);
        Assert.Equal(8080, options.PublicPort);
        Assert.Equal("dbr-worker", options.ClientCertificateCommonName);
    }

    public void Dispose()
    {
        _pki.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private InternalEdgeOptions Complete() => new()
    {
        Enabled = true,
        Port = 8443,
        PublicPort = 8080,
        ServerCertificatePath = Path.Combine(_directory, "server.crt"),
        ServerKeyPath = Path.Combine(_directory, "server.key"),
        ClientCertificateAuthorityPath = Path.Combine(_directory, "ca.crt"),
        ClientCertificateCommonName = "dbr-worker",
    };
}
