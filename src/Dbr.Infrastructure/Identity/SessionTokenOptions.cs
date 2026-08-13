// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// How long a session lasts, and what signs the tokens that carry it.
/// </summary>
public sealed class SessionTokenOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Tokens";

    /// <summary>
    /// The shortest signing key this will accept, in bytes — the output size of the
    /// hash behind HMAC-SHA256.
    /// </summary>
    /// <remarks>
    /// A shorter key does not make the algorithm refuse; it makes it weaker while
    /// continuing to work, which is the failure mode worth catching at startup.
    /// </remarks>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// The secret access tokens are signed with, and verified with.
    /// </summary>
    /// <remarks>
    /// Symmetric, because one service both issues and checks these. An asymmetric key
    /// would let something else verify without being able to mint, which matters when
    /// there is a something else; today it would only mean two keys to configure.
    /// <para>
    /// There is no default. A signing key with a value anyone can read from a public
    /// repository is a key that mints valid tokens for every deployment that never
    /// changed it, so this fails at startup instead. The compose file supplies a
    /// development value the same way it supplies a database password.
    /// </para>
    /// </remarks>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Who issued the token. Checked on the way back in.</summary>
    public string Issuer { get; set; } = "dbr";

    /// <summary>Who the token is for. Checked on the way back in.</summary>
    /// <remarks>
    /// With one service both sides of this are the same name, and checking it looks
    /// like ceremony. It costs nothing and it means a token minted by a different
    /// deployment that happens to share a signing key is refused rather than accepted.
    /// </remarks>
    public string Audience { get; set; } = "dbr";

    /// <summary>
    /// How long an access token is good for.
    /// </summary>
    /// <remarks>
    /// Short on purpose, because nothing can revoke one. An access token is checked by
    /// verifying its signature and asking the database nothing, so between signing out
    /// and this elapsing, a stolen one still works. That window is the price of not
    /// looking up a session on every request, and this is the dial that sets it.
    /// </remarks>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a refresh token is good for — how long a client may be away and still
    /// come back without signing in again.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The longest a session may live, counted from the sign-in that started it and
    /// never extended by use.
    /// </summary>
    /// <remarks>
    /// Rotation gives each new refresh token a fresh lifetime, so without this a
    /// session that keeps being used never ends — including one being kept alive by
    /// whoever stole it. This is the deadline that does not move.
    /// </remarks>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Fails startup on settings that cannot work, or that would work while being
    /// weaker than they look.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey is required and has no default — a key shipped in the "
                + "repository would mint valid tokens for every deployment that never changed it. "
                + "docker-compose.yml sets a development value; running outside compose means "
                + "supplying one. Any string of at least "
                + $"{MinimumSigningKeyBytes} random bytes will do.");
        }

        var keyBytes = Encoding.UTF8.GetByteCount(SigningKey);

        if (keyBytes < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey is {keyBytes} bytes; HMAC-SHA256 wants at least "
                + $"{MinimumSigningKeyBytes}. A shorter key does not fail, it just makes the "
                + "signature easier to forge than it appears to be.");
        }

        RequirePositive(AccessTokenLifetime, nameof(AccessTokenLifetime));
        RequirePositive(RefreshTokenLifetime, nameof(RefreshTokenLifetime));
        RequirePositive(SessionLifetime, nameof(SessionLifetime));

        if (AccessTokenLifetime >= RefreshTokenLifetime)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AccessTokenLifetime must be shorter than RefreshTokenLifetime. "
                + "The refresh token exists to outlive the access token; the other way round, "
                + "there is nothing left to refresh with by the time refreshing is needed.");
        }

        if (RefreshTokenLifetime > SessionLifetime)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RefreshTokenLifetime is longer than SessionLifetime, so the cap on "
                + "how long a session may live would never be what ends one.");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be positive; every token would be expired before it "
                + "was issued.");
        }
    }
}
