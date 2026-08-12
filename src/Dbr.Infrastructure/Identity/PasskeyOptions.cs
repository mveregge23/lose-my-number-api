// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Who this server says it is to an authenticator, and for how long a challenge
/// stands.
/// </summary>
/// <remarks>
/// A passkey is bound to the relying party it was created for and cannot be used
/// against any other, which is what makes it unphishable — and also what makes these
/// settings load-bearing. Changing <see cref="RelyingPartyId"/> after accounts exist
/// invalidates every passkey already registered, because the browser will no longer
/// offer them.
/// </remarks>
public sealed class PasskeyOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Passkeys";

    /// <summary>
    /// The domain passkeys are bound to — a bare host, no scheme and no port.
    /// </summary>
    /// <remarks>
    /// The default matches the compose stack. Any deployment reachable at a real
    /// domain has to set this, and has to set it to the registrable domain rather
    /// than the exact host if passkeys should keep working across subdomains: a
    /// credential created for <c>app.example.com</c> is offered on that host alone,
    /// while one created for <c>example.com</c> is offered on both.
    /// </remarks>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>
    /// What the authenticator shows the person when it asks them to approve. Free
    /// text, and the only part of this configuration a user ever sees.
    /// </summary>
    public string RelyingPartyName { get; set; } = "Data Broker Removal";

    /// <summary>
    /// The origins a ceremony is accepted from, each with its scheme and any port.
    /// </summary>
    /// <remarks>
    /// Checked against the origin the browser reports having performed the ceremony
    /// at. A passkey is already bound to <see cref="RelyingPartyId"/>, so this is the
    /// second half of the same guarantee rather than the whole of it.
    /// </remarks>
    public IList<string> Origins { get; set; } = ["http://localhost:8080"];

    /// <summary>
    /// How long the client has to answer a challenge before it is refused.
    /// </summary>
    /// <remarks>
    /// Long enough that someone can go and find the phone their passkey lives on,
    /// short enough that an intercepted challenge is not worth storing. The browser is
    /// told a shorter time still, since a prompt nobody is answering should give up
    /// before the server stops believing it.
    /// </remarks>
    public TimeSpan CeremonyLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fails startup on a configuration that cannot work, rather than at the first
    /// login.
    /// </summary>
    /// <remarks>
    /// Worth being strict about: every mistake below produces the same symptom, a
    /// browser refusing the ceremony with a message that does not say which setting
    /// was wrong, on a code path nobody can reach without a real authenticator.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RelyingPartyId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RelyingPartyId is required — it is the domain passkeys are bound to.");
        }

        if (Uri.TryCreate(RelyingPartyId, UriKind.Absolute, out _) || RelyingPartyId.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RelyingPartyId must be a bare host such as 'example.com', not a URL. "
                + $"Got '{RelyingPartyId}'.");
        }

        if (Origins.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Origins is empty, so every ceremony would be refused. List the "
                + "origins the browser performs ceremonies at, such as 'https://example.com'.");
        }

        foreach (var origin in Origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Origins must contain absolute http or https URLs. Got '{origin}'.");
            }

            // The browser enforces this itself and will simply refuse; catching it
            // here turns a mystery at the authenticator into a message at startup.
            var host = parsed.Host;
            var belongs = string.Equals(host, RelyingPartyId, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{RelyingPartyId}", StringComparison.OrdinalIgnoreCase);

            if (!belongs)
            {
                throw new InvalidOperationException(
                    $"{SectionName}: origin '{origin}' is not covered by RelyingPartyId "
                    + $"'{RelyingPartyId}'. A passkey is bound to the relying party's domain, so the "
                    + "origin's host has to be that domain or a subdomain of it.");
            }
        }

        if (CeremonyLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:CeremonyLifetime must be positive; every challenge would be expired "
                + "before it was answered.");
        }
    }
}
