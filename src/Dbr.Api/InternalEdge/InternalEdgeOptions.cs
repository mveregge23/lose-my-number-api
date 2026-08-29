// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Api.InternalEdge;

/// <summary>
/// The listener the workers talk to, and the certificates that decide who may.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless configured.</b> A deployment that has not been given certificates does
/// not get a degraded internal edge — it gets none, and the routes behind it are never
/// mapped anywhere. That is the safe direction for the setting to fail in, and it is why
/// there is no default certificate path: a built-in one would be a private key in a public
/// repository, which is worse than an edge that will not start.
/// </para>
/// <para>
/// <b>Both ports are named here once the edge is on.</b> Kestrel takes explicitly
/// configured endpoints over the addresses the host was launched with, and logs that it is
/// overriding them — so configuring only the internal listener would quietly stop the
/// public one from binding at all. The public port therefore has to be stated too, and its
/// default is the one the container has always listened on.
/// </para>
/// </remarks>
public sealed class InternalEdgeOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "InternalEdge";

    /// <summary>Whether the internal listener is bound at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The port the workers reach, and the one thing that decides whether a request is
    /// allowed to see the internal routes.
    /// </summary>
    /// <remarks>
    /// A local port rather than a header or a host name, because it is the only one of the
    /// three a client cannot choose. A caller can send whatever <c>Host</c> it likes; it
    /// cannot make its connection arrive on a socket it was not allowed to open.
    /// </remarks>
    public int Port { get; set; } = 8443;

    /// <summary>The port ordinary traffic arrives on.</summary>
    public int PublicPort { get; set; } = 8080;

    /// <summary>PEM certificate this listener presents.</summary>
    public string ServerCertificatePath { get; set; } = string.Empty;

    /// <summary>PEM private key for <see cref="ServerCertificatePath"/>.</summary>
    public string ServerKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// The PEM certificate authority a client certificate has to chain to.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than left to the machine's trust store. The trust store
    /// holds every public authority the operating system ships with, so validating against
    /// it would accept a certificate anybody could buy. What should be accepted here is one
    /// certificate this deployment issued to its own worker, and nothing else.
    /// </remarks>
    public string ClientCertificateAuthorityPath { get; set; } = string.Empty;

    /// <summary>
    /// The common name the client certificate must carry.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, and it is the difference between "signed by our
    /// authority" and "is our worker". An authority that has issued one certificate will
    /// eventually have issued several, and the day it issues one for something else is the
    /// day chain validation alone stops meaning what it used to.
    /// </remarks>
    public string ClientCertificateCommonName { get; set; } = string.Empty;

    /// <summary>
    /// Refuses settings no listener could work with, at startup rather than at the moment
    /// a worker first asks for somebody's identity.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            // Nothing else is read, so nothing else has to be right. An operator turning
            // this on later finds out then, which is the moment they are looking at it.
            return;
        }

        RequirePort(Port, nameof(Port));
        RequirePort(PublicPort, nameof(PublicPort));

        if (Port == PublicPort)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Port and {SectionName}:PublicPort are both {Port}. The port is "
                + "the whole of what separates the internal routes from the public ones, so one "
                + "listener serving both would put them on the open edge.");
        }

        RequireFile(ServerCertificatePath, nameof(ServerCertificatePath));
        RequireFile(ServerKeyPath, nameof(ServerKeyPath));
        RequireFile(ClientCertificateAuthorityPath, nameof(ClientCertificateAuthorityPath));

        if (string.IsNullOrWhiteSpace(ClientCertificateCommonName))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ClientCertificateCommonName is required. Without it the listener "
                + "accepts every certificate the configured authority has ever signed, which is a "
                + "wider door than anybody means to open.");
        }
    }

    private static void RequirePort(int port, string name)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} is {port}, which is not a port.");
        }
    }

    private static void RequireFile(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} is required when the internal edge is enabled.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} points at '{path}', which is not there. Failing now rather "
                + "than at the first connection: a listener that cannot prove who it is would "
                + "otherwise refuse every worker while looking like a network fault.");
        }
    }
}
