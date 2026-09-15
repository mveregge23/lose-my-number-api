// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Somebody's confirmation that a regime governs a broker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Confirmed, never inferred.</b> Whether a statute reaches a given company turns on
/// revenue and data-volume thresholds this system has no way to check, so there is no
/// safe default and no rule that could compute one. What would be produced by guessing
/// is not a guess — it is a legal position presented to somebody as though it had been
/// checked.
/// </para>
/// <para>
/// The absence of a row is therefore meaningful and safe. A broker with no confirmed
/// regime falls back to its own operational target, which is honestly labelled as one;
/// the failure of an over-eager join would be the opposite, and silent.
/// </para>
/// </remarks>
public class BrokerLegalBasis
{
    public Guid BrokerId { get; init; }

    public Guid LegalBasisId { get; init; }

    public DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>Who confirmed it, which is the whole value of the row.</summary>
    public required string ConfirmedBy { get; set; }

    /// <summary>
    /// Where it was read that the regime reaches this company — a registry entry, or the
    /// company's own notice. Required of a catalog confirmation, which is a claim shipped
    /// to every instance; optional for an operator's own, which already carries a name.
    /// </summary>
    public string? EvidenceUrl { get; set; }

    /// <summary>
    /// Whether the catalog wrote this from the company's file, or an operator did. The
    /// sync updates and removes its own to match the file and never touches the other.
    /// </summary>
    public CatalogSource Source { get; set; } = CatalogSource.Local;
}
