// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Consent;

/// <summary>
/// Reads and writes what a tenant has permitted, over a table that is only ever added to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write is an insert.</b> Revoking is a new row saying so, not an edit to the
/// row that granted. The database enforces it — the application role holds no
/// <c>UPDATE</c> on this table — so this class is where the intent lives rather than
/// where it is guarded.
/// </para>
/// <para>
/// <b>Current state is the newest row per scope</b>, which is one query rather than
/// three: a row is in force when no later row for its scope exists. That reads as a
/// <c>NOT EXISTS</c> against the same index the insert maintains, and it stays one
/// query as scopes are added.
/// </para>
/// </remarks>
public sealed class ConsentService(
    DbrDbContext core,
    ConsentPolicyOptions policy,
    ITenantContext tenantContext)
    : IConsentService
{
    public async Task<IReadOnlyList<ConsentGrant>> ReadAsync(CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);

        // Built from the enum rather than from what came back, so a scope nobody has
        // decided about is an answer rather than an absence. A client renders one switch
        // per scope and needs a position for each of them.
        return
        [
            .. Enum.GetValues<ConsentScope>()
                .Select(scope => current.TryGetValue(scope, out var record)
                    ? Grant(record)
                    : ConsentGrant.Undecided(scope)),
        ];
    }

    public async Task<RecordConsentResult> RecordAsync(
        ConsentScope scope,
        bool granted,
        string? acceptedPolicyVersion,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(acceptedPolicyVersion?.Trim(), policy.PolicyVersion, StringComparison.Ordinal))
        {
            return RecordConsentResult.Failed(RecordConsentOutcome.PolicyOutOfDate);
        }

        var tenantId = RequireTenant();
        var existing = await FindCurrentAsync(scope, cancellationToken).ConfigureAwait(false);

        if (existing is not null
            && existing.Granted == granted
            && string.Equals(existing.PolicyVersion, policy.PolicyVersion, StringComparison.Ordinal))
        {
            // Nobody decided anything. Writing a row here would put a decision in the
            // history that was never made, and the history is the reason this table is
            // shaped the way it is.
            return new RecordConsentResult(RecordConsentOutcome.Unchanged, Grant(existing));
        }

        var record = new ConsentRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Scope = scope,
            Granted = granted,
            EffectiveAt = DateTimeOffset.UtcNow,
            PolicyVersion = policy.PolicyVersion,
        };

        core.Set<ConsentRecord>().Add(record);
        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RecordConsentResult(RecordConsentOutcome.Recorded, Grant(record));
    }

    public async Task<bool> IsGrantedAsync(ConsentScope scope, CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(scope, cancellationToken).ConfigureAwait(false);

        // No decision is not permission. Nothing goes out in somebody's name because
        // they have not got around to refusing it.
        return current?.Granted ?? false;
    }

    private async Task<Dictionary<ConsentScope, ConsentRecord>> CurrentAsync(
        CancellationToken cancellationToken)
    {
        var records = await InForce()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.ToDictionary(record => record.Scope);
    }

    private async Task<ConsentRecord?> FindCurrentAsync(
        ConsentScope scope,
        CancellationToken cancellationToken) =>
        await InForce()
            .FirstOrDefaultAsync(record => record.Scope == scope, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The decision in force for each scope: the row nothing later supersedes.
    /// </summary>
    /// <remarks>
    /// Two rows for one scope cannot share an <c>EffectiveAt</c> — a unique index says
    /// so — which is what makes "nothing later" name exactly one row per scope rather
    /// than whichever of a tied pair the planner reached first.
    /// </remarks>
    private IQueryable<ConsentRecord> InForce()
    {
        var all = core.Set<ConsentRecord>();

        return all.Where(record =>
            !all.Any(later => later.Scope == record.Scope && later.EffectiveAt > record.EffectiveAt));
    }

    private static ConsentGrant Grant(ConsentRecord record) =>
        new(record.Scope, record.Granted, record.EffectiveAt, record.PolicyVersion);

    private Guid RequireTenant() =>
        tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "The consent service was asked to record a decision without a tenant. Consent is "
            + "held by exactly one account, and a scope that never established one has no "
            + "account to record it for.");
}
