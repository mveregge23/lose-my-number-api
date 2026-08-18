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
}
