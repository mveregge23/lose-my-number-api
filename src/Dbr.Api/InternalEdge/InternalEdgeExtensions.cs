// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Dbr.Api.InternalEdge;

/// <summary>
/// What the composition root calls to give the workers somewhere to call.
/// </summary>
/// <remarks>
/// <para>
/// The processes that talk to broker sites hold no keys, so anything they need from the
/// vault crosses a boundary. This is that boundary: a second listener, mutual TLS, and a
/// route table the public listener does not share.
/// </para>
/// <para>
/// <b>The internal routes are absent from the public listener, not refused by it.</b> They
/// are mapped inside a pipeline branch that only a connection arriving on the internal port
/// ever enters, so the public listener's route table has never heard of them and a request
/// for one gets the same answer as a request for a path nobody ever wrote — a plain 404,
/// indistinguishable from any other. A route that existed and answered 403 would be one
/// misconfiguration away from answering 200, and would advertise its own existence to
/// anybody scanning.
/// </para>
/// <para>
/// The branch runs the other way too: a request arriving on the internal port for a public
/// route matches nothing. The two edges are disjoint rather than nested, so neither can
/// serve the other's traffic by accident.
/// </para>
/// </remarks>
public static class InternalEdgeExtensions
{
    /// <summary>
    /// Binds the settings, and when the edge is on, the listener that serves it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The settings cannot be used. Startup is the right moment to find out: the
    /// alternative is a worker discovering it at the point somebody's removal was supposed
    /// to be going out.
    /// </exception>
    public static WebApplicationBuilder AddDbrInternalEdge(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new InternalEdgeOptions();
        builder.Configuration.GetSection(InternalEdgeOptions.SectionName).Bind(options);
        options.Validate();

        builder.Services.AddSingleton(options);

        if (!options.Enabled)
        {
            // Nothing bound, nothing mapped, nothing to reach. A deployment without
            // certificates gets no internal edge rather than a weakened one.
            return builder;
        }

        var serverCertificate = LoadServerCertificate(options);
        var gate = new InternalClientGate(
            LoadAuthority(options.ClientCertificateAuthorityPath),
            options.ClientCertificateCommonName);

        builder.Services.AddSingleton(gate);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Both endpoints, because naming one of them takes over. Kestrel prefers
            // endpoints configured here to the addresses the host was launched with and
            // says so in a log line nobody reads, so configuring only the internal
            // listener would quietly unbind the public one.
            kestrel.ListenAnyIP(options.PublicPort);

            kestrel.ListenAnyIP(options.Port, listener => listener.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;

                // Require rather than allow. A connection with no certificate is refused
                // by the handshake, so there is no request for anything downstream to
                // have an opinion about.
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                // The chain Kestrel offers was built against the machine's trust store,
                // which is not the question being asked — so it is ignored and the gate
                // builds its own against the authority this deployment named.
                https.ClientCertificateValidation =
                    (certificate, _, _) => gate.Accepts(certificate);
            }));
        });

        return builder;
    }

    /// <summary>
    /// Maps the worker-facing routes into a branch the public listener never enters.
    /// </summary>
    public static WebApplication UseDbrInternalEdge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<InternalEdgeOptions>();

        if (!options.Enabled)
        {
            return app;
        }

        app.MapWhen(IsInternalListener(options), branch =>
        {
            // Whatever the outer pipeline matched, this branch did not match it.
            //
            // A branch's routing sets an endpoint when its own table has one and leaves
            // the context alone when it does not — so a public route matched before the
            // branch was entered survives into it and gets executed here, on the internal
            // listener, by the endpoint step below. Clearing first is what makes the
            // branch's table the only one that can answer on this port, and it holds
            // wherever in the pipeline the branch ends up sitting.
            branch.Use((context, next) =>
            {
                context.SetEndpoint(null);

                return next(context);
            });

            // Its own routing, so these endpoints live in a table the public listener has
            // no reference to rather than in the shared one behind a condition.
            branch.UseRouting();
            branch.UseEndpoints(endpoints =>
            {
                endpoints.MapVaultReleaseEndpoints();
                endpoints.MapFindingEndpoints();
            });
        });

        return app;
    }

    /// <summary>
    /// Which listener a connection arrived on.
    /// </summary>
    /// <remarks>
    /// The local port, because it is the only thing about a request that the client had no
    /// say in. A header can claim anything and a host name is a header; the socket a
    /// connection landed on is a fact about the network.
    /// </remarks>
    internal static Func<HttpContext, bool> IsInternalListener(InternalEdgeOptions options) =>
        context => context.Connection.LocalPort == options.Port;

    private static X509Certificate2 LoadServerCertificate(InternalEdgeOptions options)
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(
            options.ServerCertificatePath,
            options.ServerKeyPath);

        // Round-tripped through PKCS#12 rather than handed to Kestrel as it came off disk.
        // A certificate built from PEM carries its key in a form some platforms will not
        // use for a TLS server, and the failure is at the first handshake rather than here.
        return X509CertificateLoader.LoadPkcs12(
            fromPem.Export(X509ContentType.Pkcs12),
            password: null);
    }

    private static X509Certificate2 LoadAuthority(string path) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(path));
}
