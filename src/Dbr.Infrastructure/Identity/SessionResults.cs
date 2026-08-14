// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// A signed-in session, as the client receives it.
/// </summary>
/// <param name="AccessToken">
/// Presented on every request. Checked by verifying its signature and nothing else,
/// so nothing can withdraw it before it expires.
/// </param>
/// <param name="RefreshToken">
/// Exchanged for the next pair when the access token runs out. Good exactly once —
/// the exchange replaces it — and revocable at any time.
/// </param>
public sealed record IssuedSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>How an attempt to exchange a refresh token ended.</summary>
public enum SessionRefreshOutcome
{
    /// <summary>A new pair was issued and the old refresh token is spent.</summary>
    Renewed,

    /// <summary>
    /// The token is unknown, expired, revoked, or its session has reached the cap on
    /// how long a session may live.
    /// </summary>
    /// <remarks>
    /// One value covering all of them. Which one it was is only ever useful to
    /// somebody working out whether a token they found is worth keeping.
    /// </remarks>
    Rejected,

    /// <summary>
    /// The token had already been exchanged, and the whole session has been torn down
    /// because of it.
    /// </summary>
    /// <remarks>
    /// A refresh token is spent by the exchange that replaces it, so a second
    /// presentation means two parties hold the same token — the legitimate client and
    /// somebody else. There is no way to tell which one is asking, so the only safe
    /// answer is to end the session and make both sign in again.
    /// <para>
    /// The caller is told the same thing as <see cref="Rejected"/>. This value exists
    /// so the difference is legible in the code and to tests, not so it can be
    /// reported.
    /// </para>
    /// </remarks>
    ReusedAndRevoked,

    /// <summary>
    /// The token was good, and the account is suspended.
    /// </summary>
    /// <remarks>
    /// The session is left intact rather than revoked. Suspension is not deletion, so
    /// lifting it should restore what was there — tearing the session down would make
    /// a reversible measure quietly permanent for anyone who was signed in when it
    /// happened.
    /// </remarks>
    AccountSuspended,
}

/// <param name="Session">The new pair, or <see langword="null"/> if none was issued.</param>
public sealed record SessionRefreshResult(SessionRefreshOutcome Outcome, IssuedSession? Session)
{
    public static SessionRefreshResult Failed(SessionRefreshOutcome outcome) => new(outcome, null);
}
