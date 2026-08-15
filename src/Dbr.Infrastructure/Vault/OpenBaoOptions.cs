// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// How to reach the key manager.
/// </summary>
/// <remarks>
/// Named for the product rather than for the role it plays, because a different
/// product would be a different adapter with its own settings. The core's interface
/// stays neutral; this is the part that is allowed to know what it is talking to.
/// </remarks>
public sealed class OpenBaoOptions
{
    /// <summary>
    /// The configuration section these are read from — the same one the compose file
    /// has been setting since the stack was first stood up.
    /// </summary>
    public const string SectionName = "Bao";

    /// <summary>Where the server is, scheme and port included.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The token this service authenticates with.
    /// </summary>
    /// <remarks>
    /// Today the compose stack hands over a root token, which is a development
    /// convenience and nothing more: a token that can do anything to the key manager
    /// is a token that can destroy every tenant's data. Narrowing it to encrypt and
    /// decrypt on this service's own keys is its own piece of work, and until then the
    /// security notes say plainly that this is not a deployment posture.
    /// </remarks>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Where the Transit engine is mounted. Configurable because an operator with an
    /// existing server may already have it somewhere other than the default.
    /// </summary>
    public string TransitMount { get; set; } = "transit";

    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        // The scheme has to be checked explicitly, not merely parsed. "openbao:8200"
        // is an absolute URI as far as Uri is concerned — "openbao" reads as the
        // scheme and "8200" as the path — so accepting anything that parses would
        // wave through the exact mistake this exists to catch, and turn it into a
        // confusing failure much later.
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Address must be an absolute http or https URL such as "
                + $"'http://openbao:8200'. Got '{Address}'. docker-compose.yml sets it for every "
                + "service in the stack; running outside compose means supplying it.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Token is required. Without it every attempt to protect or read "
                + "identifying data fails, and it fails at the moment somebody's data is being "
                + "written rather than at startup.");
        }

        if (string.IsNullOrWhiteSpace(TransitMount))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TransitMount is required; it is the path the engine is mounted at.");
        }
    }
}
