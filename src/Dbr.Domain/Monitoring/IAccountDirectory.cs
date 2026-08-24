// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>
/// Which accounts exist — the one question no tenant-scoped role can answer.
/// </summary>
/// <remarks>
/// <para>
/// Its own interface, with one method returning one column, because the size of this is
/// the point. Recurring work has to be planned for accounts nobody is currently acting
/// for, and that needs a privilege the rest of the system deliberately does not have. The
/// narrower the thing holding it, the easier it is to see that nothing else grew into it.
/// </para>
/// <para>
/// Everything a plan then <i>does</i> goes back through the ordinary tenant-scoped path,
/// one account at a time. This answers who; it never answers anything about them.
/// </para>
/// </remarks>
public interface IAccountDirectory
{
    /// <summary>Every account id on this instance.</summary>
    Task<IReadOnlyList<Guid>> ListAccountIdsAsync(CancellationToken cancellationToken);
}
