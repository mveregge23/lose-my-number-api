// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// One listing a scan found: this broker appears to hold data about this identity.
/// </summary>
/// <remarks>
/// <para>
/// What the finding actually matched on is not here. The pointer to the broker's own
/// profile page is a third party's copy of somebody's identity, which puts it in the
/// restricted tier alongside names and addresses, so it belongs in the vault store and
/// not on a row the ordinary API path reads. It arrives with the story that produces one
/// — nothing writes exposures yet — and until then this table deliberately has no column
/// for it, so nothing can drift into keeping it here. Recorded in KNOWN-GAPS.md.
/// </para>
/// <para>
/// <see cref="Confidence"/> is a match score and not a promise. It is what lets a client
/// sort the certain findings above the plausible ones, and what a tenant is implicitly
/// judging when they dismiss one.
/// </para>
/// </remarks>
public class Exposure : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this finding belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The run that found it.</summary>
    public required Guid ScanId { get; init; }

    /// <summary>
    /// Whose listing this is.
    /// </summary>
    /// <remarks>
    /// Denormalized from the scan that found it, and pinned to it by a key over the pair
    /// so the two cannot drift. Here rather than reached through the scan because a
    /// removal request cites both an identity and, sometimes, a listing — and without this
    /// there would be no way for the database to insist those two agree.
    /// </remarks>
    public required Guid PrivacyProfileId { get; init; }

    /// <summary>The broker it was found on.</summary>
    public required Guid BrokerId { get; init; }

    public required ExposureStatus Status { get; set; }

    /// <summary>How sure the match is, from 0 to 1.</summary>
    public required double Confidence { get; init; }

    /// <summary>When it was first found.</summary>
    public DateTimeOffset DiscoveredAt { get; init; }

    /// <summary>
    /// When a scan last confirmed it is still there, or <see langword="null"/> if none
    /// has looked again since it was found.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DiscoveredAt"/> because a listing that was confirmed
    /// present last week and one that has not been looked at since March are different
    /// answers to "is this still true", and one timestamp doing both jobs would report
    /// the second as the first.
    /// </remarks>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>
    /// A digest of the listing's address, which itself lives encrypted in the vault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here so that one listing cannot become two findings — a results page that prints the
    /// same profile twice, a redelivered report, two URLs differing by a tracking parameter —
    /// and so that a later verification scan can recognise a listing it has seen before
    /// without decrypting anything.
    /// </para>
    /// <para>
    /// <b>Not a secret, and not shown to anybody.</b> A digest of a URL is guessable for
    /// whoever already has the URL, so it proves nothing and hides nothing; it is a
    /// comparison key. It is absent from every API response for the same reason the address
    /// itself is.
    /// </para>
    /// </remarks>
    public byte[]? SourceRefDigest { get; set; }
}
