// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Whether an account is allowed to act at all.
/// </summary>
/// <remarks>
/// <para>
/// The base gate: it sits underneath whatever decides whether a paying account may
/// start new work, because suspension is a different question from monetization. A
/// self-hosted operator suspending an abusive user of their own instance and the
/// hosted instance suspending an abusive account go through this same check, and
/// neither deployment can be built without it.
/// </para>
/// <para>
/// Read through the ordinary boundary rather than through either of the lookups that
/// bypass it. By the time this is asked, the credential has already resolved and the
/// unit of work is acting for the account, so the account's own row is visible to it —
/// which means the two SECURITY DEFINER functions stay exactly as narrow as they are.
/// Widening one of them to carry a status field would have put account state within
/// reach of an unauthenticated caller for the convenience of saving a query.
/// </para>
/// </remarks>
public sealed class AccountGate(DbrDbContext context)
{
    /// <summary>
    /// Whether the account this unit of work is acting for may proceed.
    /// </summary>
    /// <remarks>
    /// An account that cannot be found answers the same as a suspended one. From here
    /// that means the row was deleted between a credential resolving and this asking,
    /// and in both cases the answer to "may this proceed" is no.
    /// </remarks>
    public async Task<bool> MayActAsync(CancellationToken cancellationToken)
    {
        var account = await context.Set<Tenant>()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return account is { Status: TenantStatus.Active };
    }
}
