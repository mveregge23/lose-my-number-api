// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.CatalogSync;

/// <summary>
/// One jurisdiction's file, as it is written.
/// </summary>
/// <remarks>
/// A file is a regime in one place, and the requests it grants are entries beneath it.
/// Grouped that way because that is the unit somebody reviews: reading California means
/// reading what the CCPA gives Californians, and splitting each request type into its own
/// file would scatter one reading across three diffs.
/// </remarks>
public sealed class LegalBasisFile
{
    public string? Code { get; set; }

    public string? ResidencyScope { get; set; }

    /// <summary>
    /// When somebody read this, and who. Recorded per file rather than per entry: the
    /// reading is of a jurisdiction, and a reviewer who checked its deletion rule but not
    /// its opt-out rule has not finished reviewing the file.
    /// </summary>
    /// <remarks>
    /// A date, deliberately, and read as midnight UTC. A review happened on a day, not at
    /// an instant — and typing it as a moment makes the same file mean different things
    /// on machines in different time zones, which for a field recording when content was
    /// last checked is a difference that quietly accumulates.
    /// </remarks>
    public DateTime? ReviewedAt { get; set; }

    public string? ReviewedBy { get; set; }

    public List<LegalBasisRequestEntry> Requests { get; set; } = [];
}

/// <summary>One kind of demand the regime grants, and what it requires.</summary>
public sealed class LegalBasisRequestEntry
{
    public string? RequestType { get; set; }

    public int? ResponseDeadlineDays { get; set; }

    /// <summary>
    /// Zero where the regime grants no extension, which is a statement rather than a
    /// blank — hence no default here. A file omitting it is refused rather than assumed
    /// to mean none.
    /// </summary>
    public int? ExtensionDays { get; set; }

    /// <summary>
    /// <c>calendar</c> or <c>business</c>. Required rather than defaulted, because the
    /// whole reason the column exists is that assuming calendar was wrong once already.
    /// </summary>
    public string? DeadlineUnit { get; set; }

    public string? VerificationLevel { get; set; }

    /// <summary>
    /// Where this was read. The one field whose absence makes a row worse than no row at
    /// all: a deadline nobody can check is a number somebody has to take on faith.
    /// </summary>
    public string? CitationUrl { get; set; }
}
