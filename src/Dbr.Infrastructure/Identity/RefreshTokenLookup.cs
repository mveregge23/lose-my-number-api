// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// The session state a refresh attempt is allowed to see before it has proved
/// anything.
/// </summary>
public sealed record StoredRefreshToken(
    Guid Id,
    Guid TenantId,
    Guid SessionId,
    DateTimeOffset SessionStartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Resolves a refresh token to its session, before the caller is acting for any
/// tenant.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as signing in, and the same narrow exception. A client refreshing
/// does so precisely because its access token has expired, so it is acting for no
/// tenant, so every policy on the token table matches nothing — including the row
/// that says who it is.
/// </para>
/// <para>
/// What keeps <c>app.find_refresh_token</c> safe is its argument: a digest of 256
/// random bits, which nobody supplies without holding the token itself. Presenting
/// the token is the authentication; this only turns it into a name.
/// </para>
/// </remarks>
public sealed class RefreshTokenLookup(DbrDbContext context)
{
    public Task<StoredRefreshToken?> FindAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        context.ExecuteCommandAsync(
            "SELECT id, tenant_id, session_id, session_started_at, expires_at, used_at, revoked_at "
            + "FROM app.find_refresh_token(@token_hash)",
            async (command, token) =>
            {
                command.WithParameter("token_hash", tokenHash);

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                return new StoredRefreshToken(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    await reader.IsDBNullAsync(5, token).ConfigureAwait(false)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(5),
                    await reader.IsDBNullAsync(6, token).ConfigureAwait(false)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(6));
            },
            cancellationToken);
}
