// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Removals;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Writes down that one leg of one scan may see part of one identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>It holds nothing that can decrypt.</b> The constructor is the claim: the core store,
/// the tenant this scope acts for, a lifetime and a clock. No vault connection, no key
/// manager, no profile service. That is what lets the process fanning a scan out across
/// broker lanes plan the work without also acquiring the ability to open it — and the day
/// somebody adds a dependency here that could, the diff says so plainly rather than the
/// capability arriving by way of a constructor two layers down.
/// </para>
/// <para>
/// In the folder its concept belongs to rather than the one its storage does. Everything
/// about a release lives here, and a minter filed under monitoring because it happens to
/// write a core-store row would separate the two halves of one idea.
/// </para>
/// </remarks>
public sealed class IdentityReleaseMinter(
    DbrDbContext core,
    TenantContext tenantContext,
    IdentityReleaseOptions options,
    TimeProvider clock)
    : IIdentityReleaseMinter
{
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

        var (token, hash) = ReleaseTokens.Create();
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
            TokenHash = hash,
            Fields = wanted,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        });

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MintReleaseResult.Minted(new MintedRelease(id, token, expiresAt));
    }

    public async Task<MintReleaseResult> MintForJobAsync(
        Guid removalJobId,
        Guid brokerId,
        IReadOnlyCollection<IdentityField> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var tenantId = RequireTenant();
        var wanted = fields.Distinct().ToArray();

        if (wanted.Length == 0)
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.NothingRequested);
        }

        // The attempt and the demand behind it, in one read. The demand is what carries
        // the identity and the company; the attempt is what the grant is scoped to, so
        // both have to be there and both have to belong to this account.
        var work = await core.Set<RemovalJob>()
            .Where(row => row.Id == removalJobId)
            .Join(
                core.Set<RemovalRequest>(),
                job => job.RemovalRequestId,
                request => request.Id,
                (job, request) => new { job.Status, request.PrivacyProfileId, request.BrokerId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (work is null)
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.JobNotFound);
        }

        // An attempt that already ran minting a fresh decryption right is the case this
        // refuses, and it is the stronger of the two runnable checks. On the scan side a
        // late leg reads a page nobody sees; here it would open an identity in order to
        // send a company a demand that has already been sent or withdrawn.
        if (work.Status is not (RemovalJobStatus.Pending or RemovalJobStatus.Running))
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.JobNotRunnable);
        }

        // No narrowing to check against, unlike a scan: a demand names exactly one company.
        // Either this is it, or the caller has confused two pieces of work.
        if (work.BrokerId != brokerId)
        {
            return MintReleaseResult.Failed(MintReleaseOutcome.BrokerNotForThisJob);
        }

        var (token, hash) = ReleaseTokens.Create();
        var now = clock.GetUtcNow();
        var expiresAt = now + options.Lifetime;
        var id = Guid.NewGuid();

        core.Set<IdentityRelease>().Add(new IdentityRelease
        {
            Id = id,
            TenantId = tenantId,
            RemovalJobId = removalJobId,
            BrokerId = brokerId,

            // Taken from the demand for the reason the scan path takes it from the run.
            PrivacyProfileId = work.PrivacyProfileId,
            TokenHash = hash,
            Fields = wanted,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        });

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MintReleaseResult.Minted(new MintedRelease(id, token, expiresAt));
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

    private Guid RequireTenant() =>
        tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "Minting a release needs a tenant, and this scope is acting for nobody. A grant "
            + "that named no account would be permission to decrypt an identity without one.");
}
