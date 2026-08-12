// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// What a login attempt is allowed to know about a passkey before it has proved
/// anything.
/// </summary>
/// <param name="TenantId">The account the passkey signs in.</param>
/// <param name="PublicKey">COSE-encoded, to check the assertion's signature against.</param>
/// <param name="SignatureCount">The counter as of the last accepted assertion.</param>
public sealed record StoredPasskey(Guid TenantId, byte[] PublicKey, long SignatureCount);

/// <summary>
/// Resolves a passkey to its account during login — the one read that happens before
/// the caller is acting for any tenant.
/// </summary>
/// <remarks>
/// <para>
/// Every policy matches zero rows for a caller who has not authenticated, which is
/// what makes the credential needed to authenticate them unreadable by ordinary
/// means. That is deliberate: a role that could read this table freely could
/// enumerate accounts. So this goes through <c>app.find_passkey</c>, a
/// SECURITY DEFINER function that runs as its owner and therefore outside the
/// policies.
/// </para>
/// <para>
/// The narrowness is the safety. It answers only to a credential id — high-entropy,
/// minted by an authenticator, and impossible to supply without holding the passkey —
/// and returns only the three values checking an assertion needs. The answer is also
/// worth nothing alone: the caller still has to sign a challenge this server issued.
/// </para>
/// </remarks>
public sealed class PasskeyLookup(DbrDbContext context)
{
    public Task<StoredPasskey?> FindAsync(
        byte[] credentialId,
        CancellationToken cancellationToken) =>
        context.ExecuteCommandAsync(
            "SELECT tenant_id, public_key, signature_count "
            + "FROM app.find_passkey(@credential_id)",
            async (command, token) =>
            {
                command.WithParameter("credential_id", credentialId);

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                return new StoredPasskey(
                    reader.GetGuid(0),
                    (byte[])reader.GetValue(1),
                    reader.GetInt64(2));
            },
            cancellationToken);
}
