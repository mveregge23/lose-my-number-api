// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Persistence;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The grant state a redemption is allowed to see before it has proved anything.
/// </summary>
/// <remarks>
/// No identifying data, deliberately. The fields themselves live encrypted in the other
/// store behind a key the key manager has to unwrap, so what this carries is the address
/// of an identity rather than any of it.
/// </remarks>
public sealed record StoredIdentityRelease(
    Guid Id,
    Guid TenantId,

    /// <summary>The run, or null when this grant is for an attempt at a removal.</summary>
    Guid? ScanId,

    /// <summary>The attempt, or null when this grant is for a leg of a scan.</summary>
    Guid? RemovalJobId,

    Guid BrokerId,
    Guid PrivacyProfileId,
    IReadOnlyList<IdentityField> Fields,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,

    /// <summary>When this leg's findings were recorded, or null while they have not been.</summary>
    /// <remarks>
    /// The grant's second single-use spend, separate from the first: opening the identity
    /// does not consume the right to say what was found with it, and reporting does not
    /// require having opened it.
    /// </remarks>
    DateTimeOffset? ReportedAt);

/// <summary>
/// Resolves a release token to the grant it opens, before the caller is acting for any
/// tenant.
/// </summary>
/// <remarks>
/// <para>
/// The third use of the same narrow exception signing in and refreshing already needed,
/// and it is narrow for the same reason. Whatever redeems a grant holds no session and
/// belongs to no account, so every policy on the table matches nothing — including the
/// row saying whose grant it is.
/// </para>
/// <para>
/// What keeps <c>app.find_identity_release</c> safe is its argument: a digest of 256
/// random bits, which nobody supplies without holding the token. Presenting the token is
/// the authentication; this only turns it into a name. That property is why the grant is
/// a minted secret rather than a scan id — a scan id is handed back to whoever asked for
/// the scan, and a function keyed on one would answer to something its holder was given
/// rather than to something they were trusted with.
/// </para>
/// </remarks>
public sealed class IdentityReleaseLookup(DbrDbContext context)
{
    public Task<StoredIdentityRelease?> FindAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        context.ExecuteCommandAsync(
            "SELECT id, tenant_id, scan_id, removal_job_id, broker_id, privacy_profile_id, fields, "
            + "expires_at, redeemed_at, reported_at FROM app.find_identity_release(@token_hash)",
            async (command, token) =>
            {
                command.WithParameter("token_hash", tokenHash);

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                // Exactly one of the two work columns is set, which the table checks. Read
                // as nullable on both sides rather than picking one and trusting the other
                // to be absent, so a row that somehow broke that rule arrives as what it
                // is instead of throwing here.
                return new StoredIdentityRelease(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    await reader.IsDBNullAsync(2, token).ConfigureAwait(false)
                        ? null
                        : reader.GetGuid(2),
                    await reader.IsDBNullAsync(3, token).ConfigureAwait(false)
                        ? null
                        : reader.GetGuid(3),
                    reader.GetGuid(4),
                    reader.GetGuid(5),
                    Fields(reader.GetFieldValue<string[]>(6)),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    await reader.IsDBNullAsync(8, token).ConfigureAwait(false)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(8),
                    await reader.IsDBNullAsync(9, token).ConfigureAwait(false)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(9));
            },
            cancellationToken);

    private static IReadOnlyList<IdentityField> Fields(string[] stored) =>
        [.. stored.Select(value =>
            IdentityVocabulary.Parse(value)
            ?? throw new InvalidOperationException(
                $"identity_release.fields holds '{value}', which this build has no value "
                + "for. Either a migration widened the check constraint ahead of the "
                + "code, or a row was written by hand."))];
}
