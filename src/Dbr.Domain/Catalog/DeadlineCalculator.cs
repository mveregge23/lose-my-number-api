// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Turns a regime's count of days into an actual date.
/// </summary>
/// <remarks>
/// <para>
/// This is the layer the catalog deliberately leaves the conversion to. A row stores the
/// number its statute prints and the unit it counts in; only here is the day the clock
/// started known, and without that a business-day count cannot be turned into anything.
/// </para>
/// <para>
/// <b>Public holidays are not skipped, and that is a known gap rather than a decision.</b>
/// Weekends fall out of the arithmetic; holidays need a source of dates per jurisdiction,
/// which nothing here has. The effect is a business-day deadline landing a day or two
/// early — the direction that reports a request overdue while the recipient still has
/// time, which is the wrong direction to be wrong in. It is written down in the
/// repository's known-gaps file rather than papered over with a guess.
/// </para>
/// </remarks>
public static class DeadlineCalculator
{
    /// <summary>
    /// The moment a regime's window closes, counting <paramref name="days"/> from
    /// <paramref name="from"/> in the given unit.
    /// </summary>
    /// <remarks>
    /// Counting starts the day after the clock starts, in both units: a statute giving
    /// forty-five days from receipt means forty-five days after the day it arrived, not
    /// forty-four and the rest of an afternoon.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The count is negative. Zero is allowed and means the window closes the moment it
    /// opens, which no regime here does but which is not this function's business to
    /// refuse.
    /// </exception>
    public static DateTimeOffset Add(DateTimeOffset from, int days, DeadlineUnit unit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);

        if (unit == DeadlineUnit.Calendar)
        {
            return from.AddDays(days);
        }

        var at = from;
        var remaining = days;

        while (remaining > 0)
        {
            at = at.AddDays(1);

            // Only weekdays count down, so a window that would have closed on a Saturday
            // closes on the following Monday instead — which is what "business days"
            // means and is why this cannot be arithmetic on the count alone.
            if (!IsWeekend(at))
            {
                remaining--;
            }
        }

        return at;
    }

    private static bool IsWeekend(DateTimeOffset at) =>
        at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
