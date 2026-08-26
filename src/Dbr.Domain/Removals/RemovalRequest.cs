// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Removals;

/// <summary>
/// One demand made of one broker on behalf of one identity, and everything that has
/// happened to it since.
/// </summary>
/// <remarks>
/// <para>
/// <b>A demand is about a person and a company, not about a listing.</b> An exposure is
/// what may have prompted it and is often absent — the right to tell a broker to delete
/// what it holds, or to stop selling it, does not depend on having found a page with your
/// name on it first.
/// </para>
/// <para>
/// A request outlives its attempts. Each attempt is a <see cref="RemovalJob"/>; this row
/// is the thing that is retried, waited on, confirmed, and — when a listing comes back —
/// resubmitted. That is why the reappearance loop returns here rather than opening a
/// second request: the number of times a broker has re-listed somebody is a fact about
/// this demand, and starting fresh each time would throw it away.
/// </para>
/// <para>
/// <b>The deadline is snapshotted, not recomputed.</b> §11.2 resolves which regime governs
/// at the moment the request is created, and the answer is written here with the citation
/// behind it. A statute corrected next year must not silently reinterpret what somebody
/// was told this year.
/// </para>
/// </remarks>
public class RemovalRequest : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this demand is made for.</summary>
    public Guid TenantId { get; init; }

    /// <summary>
    /// Whose data is being demanded gone.
    /// </summary>
    /// <remarks>
    /// The subject of the demand, and the reason it can exist without a listing. It is
    /// also what a connector needs in order to know whose details to ask the vault for —
    /// a question that has no answer if the identity is only reachable through an exposure
    /// that may not be there.
    /// </remarks>
    public required Guid PrivacyProfileId { get; init; }

    /// <summary>
    /// The listing that prompted this demand, or <see langword="null"/> when none did.
    /// </summary>
    /// <remarks>
    /// Evidence rather than subject. Nothing about the right to make the demand depends on
    /// having found something first: a deletion request does not oblige somebody to prove
    /// the company holds their data, and an opt-out of sale is prospective — it is a
    /// meaningful thing to say to a company whose search page returns nothing today. A
    /// scan only finds what is publicly searchable, and asking only where a search got a
    /// hit means declining to ask about the rest.
    /// </remarks>
    public Guid? ExposureId { get; init; }

    /// <summary>Which right is being exercised.</summary>
    /// <remarks>
    /// Recorded rather than implied. §11.2 resolves the governing regime by intersecting
    /// residency, the broker's confirmed statutes and the kind of demand — deletion and
    /// opt-out are different rights carrying different deadlines under the same statute —
    /// so a deadline that did not record which demand it was computed for cannot be read
    /// back against it.
    /// </remarks>
    public required LegalRequestType RequestType { get; init; }

    /// <summary>
    /// The company being asked.
    /// </summary>
    /// <remarks>
    /// Carried here as well as on the exposure so a dispatcher can route by broker without
    /// a join on the busiest path it has. The database holds the two in agreement — the
    /// foreign key is over the exposure and its broker together — so this is a shortcut
    /// rather than a second opinion.
    /// </remarks>
    public required Guid BrokerId { get; init; }

    public required RemovalRequestStatus Status { get; set; }

    /// <summary>How it is carried out, taken from the broker's catalog entry.</summary>
    public required RemovalStrategy Strategy { get; init; }

    /// <summary>How many attempts have been made.</summary>
    public required int Attempt { get; set; }

    /// <summary>
    /// The regime that governed, or <see langword="null"/> when none did.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and not a missing one: it means no confirmed statute reached
    /// this company for this person, so the deadline below is the broker's own courtesy
    /// target. Which of the two it is, is the point of the next field.
    /// </remarks>
    public Guid? LegalBasisId { get; init; }

    /// <summary>Whether a statute set the deadline or the broker's own target did.</summary>
    /// <remarks>
    /// Recorded rather than inferred from whether <see cref="LegalBasisId"/> is set,
    /// because somebody reading a date needs to know whether missing it is disappointing
    /// or actionable, and that is not a distinction to leave to a null check.
    /// </remarks>
    public required DeadlineSource DeadlineSource { get; init; }

    /// <summary>When the answer is due.</summary>
    public required DateTimeOffset DeadlineAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
