// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.InternalEdge;
using Microsoft.Extensions.Configuration;

namespace Dbr.Infrastructure.Tests.InternalEdge;

/// <summary>
/// What a worker refuses to start on before it can reach the internal edge.
/// </summary>
/// <remarks>
/// The files here only have to exist — whether they parse is the loader's question, and a
/// separate one. What this covers is the settings, where the mistakes are a missing path
/// and, the one worth refusing loudest, an address that is not https.
/// </remarks>
public class InternalClientOptionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"dbr-client-options-{Guid.NewGuid():N}");

    public InternalClientOptionsTests()
    {
        Directory.CreateDirectory(_directory);

        foreach (var name in (string[])["worker.crt", "worker.key", "ca.crt"])
        {
            File.WriteAllText(Path.Combine(_directory, name), "not parsed here");
        }
    }

    [Fact]
    public void Saying_nothing_asks_for_nothing()
    {
        var options = new InternalClientOptions();

        options.Validate();

        Assert.False(options.Enabled);
    }

    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Complete().Validate();
    }

    /// <summary>
    /// Plain http is refused rather than allowed with a warning.
    /// </summary>
    /// <remarks>
    /// What crosses this connection is a grant token on the way out and somebody's
    /// decrypted name on the way back. Over http both are readable by anything on the
    /// path, and mutual TLS is not a thing that can be partly configured — without it the
    /// listener would refuse the connection anyway, so the only effect of allowing http
    /// here would be a confusing failure instead of a clear one.
    /// </remarks>
    [Theory]
    [InlineData("http://api:8443")]
    [InlineData("api:8443")]
    [InlineData("")]
    [InlineData("not a url")]
    public void An_address_that_is_not_https_is_refused(string address)
    {
        var options = Complete();
        options.BaseAddress = address;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("https", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(InternalClientOptions.ClientCertificatePath))]
    [InlineData(nameof(InternalClientOptions.ClientKeyPath))]
    [InlineData(nameof(InternalClientOptions.ServerCertificateAuthorityPath))]
    public void Every_path_is_required(string property)
    {
        var options = Complete();
        typeof(InternalClientOptions).GetProperty(property)!.SetValue(options, string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(property, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authority is required, and it is the one somebody would think optional.
    /// </summary>
    /// <remarks>
    /// Without it a worker would have to trust whatever answered the address, which makes
    /// the handshake one-directional in practice: anything that could occupy the address
    /// gets handed a valid grant token, and the token is the credential.
    /// </remarks>
    [Fact]
    public void The_server_authority_is_not_optional()
    {
        var options = Complete();
        options.ServerCertificateAuthorityPath = string.Empty;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_path_that_is_not_there_is_refused()
    {
        var options = Complete();
        options.ClientCertificatePath = Path.Combine(_directory, "absent.crt");

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void The_settings_bind_from_the_shape_compose_writes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Enabled"] = "true",
                ["InternalApi:BaseAddress"] = "https://api:8443",
                ["InternalApi:ClientCertificatePath"] = Path.Combine(_directory, "worker.crt"),
                ["InternalApi:ClientKeyPath"] = Path.Combine(_directory, "worker.key"),
                ["InternalApi:ServerCertificateAuthorityPath"] = Path.Combine(_directory, "ca.crt"),
            })
            .Build();

        var options = new InternalClientOptions();
        configuration.GetSection(InternalClientOptions.SectionName).Bind(options);

        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal("https://api:8443", options.BaseAddress);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private InternalClientOptions Complete() => new()
    {
        Enabled = true,
        BaseAddress = "https://api:8443",
        ClientCertificatePath = Path.Combine(_directory, "worker.crt"),
        ClientKeyPath = Path.Combine(_directory, "worker.key"),
        ServerCertificateAuthorityPath = Path.Combine(_directory, "ca.crt"),
    };
}
