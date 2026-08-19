// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// One regime, for one kind of request, protecting one place: what it requires and
/// where that was read.
/// </summary>
/// <remarks>
/// <para>
/// Reviewed content the dispatcher computes with, never a legal conclusion written into
/// application code. A deadline that moves because a statute was amended should be a row
/// somebody edited and a reviewer signed off, not a constant somebody has to find.
/// </para>
/// <para>
/// <b>Provenance is required, not decoration.</b> Every row carries the source it was
/// read from, when, and by whom. A row that cannot say those things is worse than no row
/// at all: with no row, a removal falls back to the broker's own target and is labelled
/// as a courtesy — with a bad row, somebody is told they have a legal deadline they may
/// not have.
/// </para>
/// <para>
/// Shared reference data, so it is not tenant-scoped and the application may only read
/// it. What it protects is expressed as a region rather than as a person, which is what
/// lets a removal work out which regimes apply without decrypting anything.
/// </para>
/// </remarks>
public class LegalBasis
{
    public Guid Id { get; init; }

    /// <summary>The regime as it is cited — <c>CCPA</c>, <c>GDPR</c>, <c>VCDPA</c>.</summary>
    public required string Code { get; set; }

    public required LegalRequestType RequestType { get; set; }

    /// <summary>
    /// Who the regime protects, as a coarse region code — <c>US-CA</c>, <c>EU</c>.
    /// </summary>
    /// <remarks>
    /// The same shape a profile records residency in, and that is load-bearing rather
    /// than tidy: the two are compared directly when a request resolves which regimes
    /// govern it, so a second spelling here would match nothing and fail as a missing
    /// statute rather than as an error.
    /// </remarks>
    public required string ResidencyScope { get; set; }

    /// <summary>The statutory window to answer in, counted in <see cref="DeadlineUnit"/>.</summary>
    public required int ResponseDeadlineDays { get; set; }

    /// <summary>
    /// Whether this regime's days are calendar days or business days.
    /// </summary>
    /// <remarks>
    /// Governs <see cref="ExtensionDays"/> as well: an extension is more of the same
    /// regime's time. Defaults to calendar, which is what a statute means when it does
    /// not say — but it cannot be assumed, because at least one of these counts the
    /// other way and the difference is most of a week.
    /// </remarks>
    public DeadlineUnit DeadlineUnit { get; set; } = DeadlineUnit.Calendar;

    /// <summary>
    /// A one-time extension where the regime allows one; zero where it does not.
    /// </summary>
    /// <remarks>
    /// Zero is an answer, which is why this is not nullable — "this regime grants no
    /// extension" and "nobody filled this in" want to be told apart, and only one of
    /// them should be storable.
    /// </remarks>
    public int ExtensionDays { get; set; }

    public required VerificationLevel VerificationLevel { get; set; }

    /// <summary>The primary source this row was read from.</summary>
    public required string CitationUrl { get; set; }

    public DateTimeOffset ReviewedAt { get; set; }

    /// <summary>Who read it.</summary>
    public required string ReviewedBy { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
