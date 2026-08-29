// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Dbr.Infrastructure.InternalEdge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a worker's composition root calls to be able to spend a grant.
/// </summary>
/// <remarks>
/// This is the only thing a key-less process gets, and it is worth saying what it is not:
/// not a vault connection, not a key-manager token, not a way to ask what exists. It is a
/// client that can present one grant at a time to one address, holding a certificate that
/// says which machine it is.
/// </remarks>
public static class InternalApiServiceCollectionExtensions
{
    public static IServiceCollection AddDbrInternalApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new InternalClientOptions();
        configuration.GetSection(InternalClientOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);

        if (!options.Enabled)
        {
            // No client at all rather than one that cannot connect. Something resolving
            // IReleaseClient in a deployment that never configured the edge should fail
            // where it is wired up, not at the first grant it tries to spend.
            return services;
        }

        var clientCertificate = LoadClientCertificate(options);
        var authority = X509Certificate2.CreateFromPem(
            File.ReadAllText(options.ServerCertificateAuthorityPath));

        services
            .AddHttpClient<IReleaseClient, InternalReleaseClient>(client =>
                client.BaseAddress = new Uri(options.BaseAddress))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = [clientCertificate],

                    // A chain policy rather than a validation callback, and the difference
                    // matters. Supplying a callback replaces every check TLS would have
                    // made, including that the certificate was issued for the host being
                    // dialled — so the usual "trust our own authority" callback quietly
                    // also accepts our authority's certificate for a different name. This
                    // swaps the trust anchor and leaves the rest of the verification alone.
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        CustomTrustStore = { authority },

                        // A private authority for a handful of certificates publishes no
                        // revocation list, so requiring one would fail every handshake.
                        // The same limitation the listener carries, recorded in both places
                        // because it is a property of the deployment rather than of a side.
                        RevocationMode = X509RevocationMode.NoCheck,
                    },
                },
            });

        return services;
    }

    private static X509Certificate2 LoadClientCertificate(InternalClientOptions options)
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(
            options.ClientCertificatePath,
            options.ClientKeyPath);

        // Round-tripped through PKCS#12: a certificate built straight from PEM carries its
        // private key in a form some platforms decline to use for client authentication,
        // and that shows up as a handshake failure rather than as a load error.
        return X509CertificateLoader.LoadPkcs12(
            fromPem.Export(X509ContentType.Pkcs12),
            password: null);
    }
}
