// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Reads the shared catalog: which brokers are known, and which statutes have been
/// confirmed to govern them.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and not because nothing needs writing yet. Curated content arrives by a
/// reviewed path with the privileges migrations run with, and the role behind this
/// interface holds <c>SELECT</c> and nothing else — so a write method here would not
/// compile into anything that works.
/// </para>
/// <para>
/// Nothing this returns belongs to an account, which makes it the one service in the
/// codebase that answers the same thing for everybody, including a caller with no
/// account at all.
/// </para>
/// </remarks>
public interface ICatalogService
{
    /// <summary>The active brokers matching a filter, ordered for display.</summary>
    Task<IReadOnlyList<Broker>> ListBrokersAsync(
        BrokerFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// One broker with its confirmed regimes, or <see langword="null"/> if the catalog
    /// has no such entry.
    /// </summary>
    /// <remarks>
    /// Answers for a deactivated entry too. A client holding a link to a broker the
    /// operator has since stopped dispatching against should be told that is what
    /// happened, rather than told it never existed.
    /// </remarks>
    Task<BrokerEntry?> FindBrokerAsync(Guid brokerId, CancellationToken cancellationToken);

    /// <summary>The regimes matching a filter, ordered for display.</summary>
    Task<IReadOnlyList<LegalBasis>> ListLegalBasesAsync(
        LegalBasisFilter filter,
        CancellationToken cancellationToken);

    /// <summary>One regime, or <see langword="null"/> if there is no such row.</summary>
    Task<LegalBasis?> FindLegalBasisAsync(Guid legalBasisId, CancellationToken cancellationToken);
}
