// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Which brokers to list.
/// </summary>
/// <remarks>
/// Both filters are optional and combine, which is the question somebody actually has:
/// "which brokers that a statute I am covered by applies to will take an email". Neither
/// widens what is returned — an entry the operator has deactivated stays out regardless,
/// because a list is what a client dispatches against.
/// </remarks>
/// <param name="RemovalMethod">Only brokers taking requests this way.</param>
/// <param name="LegalBasisId">
/// Only brokers somebody has confirmed this regime governs. Confirmations are reviewed
/// rather than computed, so an empty answer means nobody has confirmed it yet — not that
/// the statute does not reach anybody.
/// </param>
public readonly record struct BrokerFilter(RemovalMethod? RemovalMethod, Guid? LegalBasisId);

/// <param name="ResidencyScope">Only regimes protecting this region, normalized.</param>
/// <param name="RequestType">Only regimes granting this kind of demand.</param>
public readonly record struct LegalBasisFilter(string? ResidencyScope, LegalRequestType? RequestType);

/// <summary>
/// One broker and the regimes somebody has confirmed govern it.
/// </summary>
/// <remarks>
/// The confirmations come with the detail rather than being a route of their own: they
/// are the part of a broker entry that explains the deadline a removal against it would
/// get, and a client that has to fetch them separately will show the deadline without
/// them.
/// </remarks>
public sealed record BrokerEntry(Broker Broker, IReadOnlyList<ConfirmedRegime> Regimes);

/// <summary>
/// When a request has to be answered by, and on whose authority.
/// </summary>
/// <remarks>
/// The two fields are answered together on purpose. A date on its own cannot say whether
/// missing it is disappointing or actionable, and that difference is the whole reason the
/// catalog carries statutes at all.
/// </remarks>
/// <param name="DeadlineAt">The moment the window closes.</param>
/// <param name="Source">Whether a statute set that date or the broker's own target did.</param>
/// <param name="LegalBasisId">
/// The regime that governed, or <see langword="null"/> when none did. Recorded so that a
/// deadline can be traced back to the row — and the citation on it — that produced the
/// number.
/// </param>
public sealed record DeadlineResolution(
    DateTimeOffset DeadlineAt,
    DeadlineSource Source,
    Guid? LegalBasisId);

/// <summary>
/// A regime, and the record of who decided it applies here.
/// </summary>
/// <remarks>
/// Both halves travel together because neither is worth much alone. The regime says what
/// the deadline would be; the confirmation says that a person, on a date, judged it to
/// reach this company — which is the part no code could work out.
/// </remarks>
public sealed record ConfirmedRegime(LegalBasis Basis, DateTimeOffset ConfirmedAt, string ConfirmedBy);
