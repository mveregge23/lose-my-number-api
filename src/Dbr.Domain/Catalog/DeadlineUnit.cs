// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// How a regime counts the days it gives.
/// </summary>
/// <remarks>
/// <para>
/// Almost every statute here answers in calendar days and never says so, which is why
/// this defaults rather than being required. California's opt-out rule is the exception
/// that makes it necessary: it is counted in business days, and storing the converted
/// number instead would put a figure in the row that is not the figure in the statute
/// beside it.
/// </para>
/// <para>
/// The conversion belongs wherever a date is worked out, because that is the only place
/// holding the date the clock started — and weekends and public holidays cannot be
/// counted without it.
/// </para>
/// </remarks>
public enum DeadlineUnit
{
    /// <summary>Every day counts, which is what a regime means when it says nothing.</summary>
    Calendar,

    /// <summary>Weekends and public holidays do not count.</summary>
    Business,
}
