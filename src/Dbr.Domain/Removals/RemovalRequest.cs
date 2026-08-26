// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Removals;

/// <summary>
/// One demand made of one broker about one listing, and everything that has happened to
/// it since.
/// </summary>
/// <remarks>
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

    /// <summary>The listing being demanded gone.</summary>
    public required Guid ExposureId { get; init; }

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
