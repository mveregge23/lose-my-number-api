// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Catalog;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Mints the permission to see part of an identity, and spends it.
/// </summary>
/// <remarks>
/// <para>
/// The two halves run in different places, which is the point of splitting them. Minting
/// happens where the tenant is already established and the work is being planned;
/// redeeming happens on behalf of a process that holds no keys, no vault connection and
/// no session, and is therefore acting for nobody until this resolves the token it
/// presented.
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
    DbrDbContext core,
    IProfileService profiles,
    IdentityReleaseLookup lookup,
    TenantContext tenantContext,
    IdentityReleaseOptions options,
    TimeProvider clock)
    : IIdentityReleaseService
{
    /// <summary>
    /// 256 bits, the same size as a refresh token and for the same reason: it is the
    /// whole of what stands between somebody presenting a grant and holding one.
    /// </summary>
    private const int TokenBytes = 32;

    public async Task<MintReleaseResult> MintAsync(
        Guid scanId,
        Guid brokerId,
        IReadOnlyCollection<IdentityField> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var tenantId = RequireTenant();

        // Deduplicated here rather than refused, and the database cannot check it: saying
        // "no duplicates" in a CHECK constraint needs a subquery. A repeated group would
        // decrypt the same field twice rather than widen anything, so the tidy answer is
        // to make it not happen rather than to make it an error somebody has to handle.
        var wanted = fields.Distinct().ToArray();

        if (wanted.Length == 0)
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.NothingRequested);
        }

        var scan = await core.Set<Scan>()
            .FirstOrDefaultAsync(row => row.Id == scanId, cancellationToken)
            .ConfigureAwait(false);

        if (scan is null)
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.ScanNotFound);
        }

        // A finished run minting a fresh decryption right is the case this refuses. Work
        // arriving late — a lane draining after the run was marked failed — would
        // otherwise still be able to open somebody's identity, and the scan it named
        // would already have been reported as over.
        if (scan.Status is not (ScanStatus.Queued or ScanStatus.Running))
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.ScanNotRunnable);
        }

        var refusal = await CheckBrokerAsync(scanId, brokerId, cancellationToken).ConfigureAwait(false);

        if (refusal is { } outcome)
        {
            return MintReleaseResult.Failed(outcome);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = clock.GetUtcNow();
        var expiresAt = now + options.Lifetime;
        var id = Guid.NewGuid();

        core.Set<IdentityRelease>().Add(new IdentityRelease
        {
            Id = id,
            TenantId = tenantId,
            ScanId = scanId,
            BrokerId = brokerId,

            // Taken from the scan rather than from the caller. A profile id in the
            // signature would be a second chance to name the wrong identity, and the run
            // already settled which one is being searched for.
            PrivacyProfileId = scan.PrivacyProfileId,
            TokenHash = Digest(token),
            Fields = wanted,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        });

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MintReleaseResult.Minted(new MintedRelease(id, token, expiresAt));
    }

    public async Task<RedeemReleaseResult> RedeemAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Runs before any tenant is established, over a connection this closes again
        // afterwards — so the ordinary queries below open a fresh one carrying the tenant
        // this just resolved, rather than the empty one the lookup arrived with.
        var stored = await lookup.FindAsync(Digest(token), cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Whether this broker is one this scan may mint a grant for.
    /// </summary>
    /// <remarks>
    /// A scan with no rows narrowing it means the whole catalog, so the question is
    /// whether the company is in it and active. A narrowed scan is a statement about who
    /// gets asked, and minting for a company outside that list would decrypt an identity
    /// for a leg the tenant declined.
    /// </remarks>
    private async Task<MintReleaseOutcome?> CheckBrokerAsync(
        Guid scanId,
        Guid brokerId,
        CancellationToken cancellationToken)
    {
        var narrowed = await core.Set<ScanBroker>()
            .Where(row => row.ScanId == scanId)
            .Select(row => row.BrokerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (narrowed.Count > 0)
        {
            return narrowed.Contains(brokerId) ? null : MintReleaseOutcome.BrokerNotInScan;
        }

        var known = await core.Set<Broker>()
            .AnyAsync(row => row.Id == brokerId && row.Active, cancellationToken)
            .ConfigureAwait(false);

        return known ? null : MintReleaseOutcome.UnknownBroker;
    }

    private static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private Guid RequireTenant() =>
        tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "Minting a release needs a tenant, and this scope is acting for nobody. A grant "
            + "that named no account would be permission to decrypt an identity without one.");
}
