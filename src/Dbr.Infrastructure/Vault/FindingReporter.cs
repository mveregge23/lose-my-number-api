// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Search;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Writes down what a leg found, on behalf of a process that holds no keys.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of redeeming a release, one step later in the same leg and authenticated by the
/// same grant. A finding carries the address of the listing it was found on, and a broker's
/// profile URL routinely spells out the name and the city of the person it is about — so the
/// link is a copy of the identity rather than a pointer to one, and the process that talks to
/// broker sites can no more write it than it can read a name.
/// </para>
/// <para>
/// <b>The two rows go to two stores, and the vault one is written first.</b> There is no
/// transaction spanning both — that is the point of the boundary, and the day the vault moves
/// to its own database it stops being imaginable. So the order is chosen for which failure is
/// survivable: a vault row with no exposure beside it is unreferenced, unreadable and
/// cryptographically erased with its tenant, while an exposure with no source would be a
/// finding nobody can look at and nothing can verify.
/// </para>
/// <para>
/// <b>The floor is applied here rather than at the leg.</b> A worker reports what it saw; what
/// is worth keeping is decided by the process that keeps it, so a leg cannot decide that
/// something below the bar is worth writing down.
/// </para>
/// </remarks>
public sealed class FindingReporter(
    DbrDbContext core,
    VaultDbContext vault,
    IKeyManagementProvider keys,
    IdentityReleaseLookup lookup,
    TenantContext tenantContext,
    TimeProvider clock)
    : IFindingReporter
{
    public async Task<ReportFindingsResult> ReportAsync(
        string token,
        IReadOnlyList<ReportedListing> listings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(listings);

        // Resolved before any tenant is established, over a connection this closes again —
        // the same narrow definer function redemption uses, keyed on a digest of the token.
        var stored = await lookup
            .FindAsync(ReleaseTokens.Digest(token), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return ReportFindingsResult.Refused();
        }

        var now = clock.GetUtcNow();

        tenantContext.SetTenant(stored.TenantId);

        // <b>The claim is the only thing that decides, and it is deliberately the only thing.</b>
        // The redemption path beside this one reads the grant's state first and refuses early,
        // which saves a pointless write — and which also shadows its own claim so completely
        // that deleting the claim's condition broke none of its tests until somebody wrote a
        // race to catch it. The same shape here behaved the same way: sixteen callers released
        // together still queued up behind the read.
        //
        // So the read above resolves which leg the token belongs to and decides nothing.
        // Whether this grant may still report is asked once, in the statement that spends it,
        // and the sequential test is enough to hold it because there is nothing in front of it
        // to answer first.
        var claimed = await core.Set<IdentityRelease>()
            .Where(row => row.Id == stored.Id && row.ReportedAt == null && row.ExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.ReportedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
        {
            return ReportFindingsResult.Refused();
        }

        var recorded = 0;
        var belowFloor = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var listing in listings)
        {
            var confidence = MatchConfidence.Score(listing.Matches);

            if (!MatchConfidence.ClearsFloor(confidence))
            {
                belowFloor++;

                continue;
            }

            // One listing is one candidate. The search contract already refuses a result
            // carrying the same reference twice, and this handles what the contract cannot
            // see: a report assembled from more than one page, or a worker that repeated
            // itself. The constraint on the table is the backstop rather than the check.
            if (!seen.Add(listing.SourceRef.AbsoluteUri))
            {
                continue;
            }

            await RecordAsync(stored, listing, confidence, now, cancellationToken)
                .ConfigureAwait(false);

            recorded++;
        }

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ReportFindingsResult(ReportFindingsOutcome.Recorded, recorded, belowFloor);
    }

    /// <summary>One finding: its address in the vault, then the row that points at it.</summary>
    private async Task RecordAsync(
        StoredIdentityRelease grant,
        ReportedListing listing,
        double confidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exposureId = Guid.NewGuid();

        // A data key per finding rather than one shared across an account's findings.
        // Findings arrive one at a time over months and are purged one at a time as removals
        // complete, and a shared key would be kept alive by the last survivor on behalf of
        // everything already gone.
        var generated = await keys
            .GenerateDataKeyAsync(grant.TenantId, cancellationToken)
            .ConfigureAwait(false);

        using (generated.Key)
        {
            vault.Set<ExposureSource>().Add(new ExposureSource
            {
                ExposureId = exposureId,
                TenantId = grant.TenantId,
                WrappedDataKey = generated.Wrapped,
                EncryptedSourceRef = ExposureSourceCipher.Encrypt(
                    generated.Key,
                    new ExposureSourceBinding(grant.TenantId, exposureId),
                    listing.SourceRef.AbsoluteUri),
            });
        }

        // Written before the row that references it, so the survivable failure is the one
        // that happens: an unreferenced vault row rather than a finding nobody can look at.
        await vault.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        core.Set<Exposure>().Add(new Exposure
        {
            Id = exposureId,
            TenantId = grant.TenantId,
            ScanId = grant.ScanId,
            PrivacyProfileId = grant.PrivacyProfileId,
            BrokerId = grant.BrokerId,
            Status = ExposureStatus.New,
            Confidence = confidence,
            DiscoveredAt = now,
            SourceRefDigest = ExposureSourceCipher.Digest(listing.SourceRef),
        });
    }
}
