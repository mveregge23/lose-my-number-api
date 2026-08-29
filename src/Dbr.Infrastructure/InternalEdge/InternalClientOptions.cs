// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.InternalEdge;

/// <summary>
/// How a worker reaches the process that holds the keys.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the listener's settings, and deliberately a separate section: the two
/// live in different processes with different certificates, and a shared block would
/// suggest a worker could read the server's private key because it appeared in the same
/// place.
/// </para>
/// <para>
/// <b>Off unless configured</b>, like the listener. A worker without certificates is one
/// that cannot ask for an identity, which is the correct behaviour for a deployment that
/// has not set the edge up — better than one that starts, accepts work, and fails at the
/// moment it matters.
/// </para>
/// </remarks>
public sealed class InternalClientOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "InternalApi";

    /// <summary>Whether this process can reach the internal edge at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where the internal listener is, scheme and port included.</summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>PEM certificate this worker presents.</summary>
    public string ClientCertificatePath { get; set; } = string.Empty;

    /// <summary>PEM private key for <see cref="ClientCertificatePath"/>.</summary>
    public string ClientKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// The PEM authority the listener's own certificate has to chain to.
    /// </summary>
    /// <remarks>
    /// A worker validates the server as strictly as the server validates it. Skipping this
    /// would make mutual TLS one-directional in practice: anything that could occupy the
    /// address would be handed a valid grant token, which is the credential the whole
    /// arrangement exists to protect.
    /// </remarks>
    public string ServerCertificateAuthorityPath { get; set; } = string.Empty;

    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BaseAddress must be an absolute https URL such as "
                + $"'https://api:8443'. Got '{BaseAddress}'. Plain http would put a grant token "
                + "and somebody's decrypted identity on the wire in the clear.");
        }

        RequireFile(ClientCertificatePath, nameof(ClientCertificatePath));
        RequireFile(ClientKeyPath, nameof(ClientKeyPath));
        RequireFile(ServerCertificateAuthorityPath, nameof(ServerCertificateAuthorityPath));
    }

    private static void RequireFile(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} is required when this process is to reach the internal "
                + "edge.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} points at '{path}', which is not there. A worker that "
                + "cannot prove which machine it is will be refused at the handshake, and that "
                + "reads as a network problem rather than a missing file.");
        }
    }
}
