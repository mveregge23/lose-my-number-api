// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Spends a grant, and mints one for the process that can do both.
/// </summary>
/// <remarks>
/// <para>
/// Redeeming is the whole of what this class does itself. Minting is delegated to
/// <see cref="IdentityReleaseMinter"/> — which is not a layer for its own sake: the two
/// halves need different privileges, and a process that plans work has to be able to take
/// the minting one without the vault connection this class holds. What is left here is the
/// half that decrypts, and it exists only where the keys do.
/// </para>
/// <para>
/// <b>Redeeming spends the grant before it decrypts anything.</b> If the decryption then
/// fails, the grant is gone and the work has to be re-planned — which is the right way
/// round. The alternative leaves a token that failed once available to be presented
/// again, and "it did not work the first time" is exactly the story somebody probing
/// would tell.
/// </para>
/// </remarks>
public sealed class IdentityReleaseService(
    IIdentityReleaseMinter minter,
    DbrDbContext core,
    IProfileService profiles,
    IdentityReleaseLookup lookup,
    TenantContext tenantContext,
    TimeProvider clock)
    : IIdentityReleaseService
{
    public Task<MintReleaseResult> MintAsync(
        Guid scanId,
        Guid brokerId,
        IReadOnlyCollection<IdentityField> fields,
        CancellationToken cancellationToken) =>
        minter.MintAsync(scanId, brokerId, fields, cancellationToken);

    public async Task<RedeemReleaseResult> RedeemAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Runs before any tenant is established, over a connection this closes again
        // afterwards — so the ordinary queries below open a fresh one carrying the tenant
        // this just resolved, rather than the empty one the lookup arrived with.
        var stored = await lookup
            .FindAsync(ReleaseTokens.Digest(token), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return RedeemReleaseResult.Refused();
        }

        var now = clock.GetUtcNow();

        if (stored.RedeemedAt is not null || stored.ExpiresAt <= now)
        {
            return RedeemReleaseResult.Refused();
        }

        tenantContext.SetTenant(stored.TenantId);

        // The claim and the check are one statement, and this is what actually makes a
        // grant single-use. The read above is a courtesy that saves a pointless write on
        // the ordinary case; it decides nothing, because two callers arriving together
        // both see an unspent grant there. The conditions are repeated here for that
        // reason rather than out of caution — what was read a moment ago is what the
        // grant looked like a moment ago, and this is the read the database serialises.
        var claimed = await core.Set<IdentityRelease>()
            .Where(row => row.Id == stored.Id && row.RedeemedAt == null && row.ExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.RedeemedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
        {
            return RedeemReleaseResult.Refused();
        }

        var identity = await profiles
            .ReadIdentityAsync(stored.PrivacyProfileId, stored.Fields, cancellationToken)
            .ConfigureAwait(false);

        if (identity is null)
        {
            // The grant stays spent. A profile that vanished between minting and
            // redeeming is a real situation — an account deleted mid-scan — and the
            // answer to it is that this grant is over, not that it may be tried again.
            return RedeemReleaseResult.Refused();
        }

        return RedeemReleaseResult.Granted(
            new RedeemedRelease(stored.ScanId, stored.BrokerId, stored.Fields, identity));
    }
}
