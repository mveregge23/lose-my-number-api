// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// When the recurring scan planner wakes up, and whether it does at all.
/// </summary>
/// <remarks>
/// The day an account's scan lands on is not configurable — it is derived from the
/// account id so that the spread cannot be turned off by accident. What an operator
/// chooses is the hour of the day the planner runs, which is a fact about their
/// infrastructure rather than about anybody's schedule.
/// </remarks>
public sealed class ScanScheduleOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "ScanSchedule";

    /// <summary>
    /// Whether the planner runs.
    /// </summary>
    /// <remarks>
    /// On by default: monthly monitoring is the product rather than a feature to opt into,
    /// and an instance where it silently never ran would look identical to one where
    /// nobody was ever listed anywhere. An operator with a reason to stop it — a staging
    /// copy of production data, say — turns it off explicitly.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The hour, UTC, at which the planner looks for accounts due today.
    /// </summary>
    /// <remarks>
    /// Defaults to the small hours, where a burst of queued work has the rest of the day
    /// to drain through the per-broker lanes before anybody is watching. UTC rather than
    /// local, so the same configuration means the same thing on every machine the stack
    /// is deployed to.
    /// </remarks>
    public int DailyAtHourUtc { get; set; } = 2;

    /// <summary>
    /// Fails startup on a value the planner could not run with, rather than at the first
    /// fire that never comes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (DailyAtHourUtc is < 0 or > 23)
        {
            throw new InvalidOperationException(
                $"{SectionName}:DailyAtHourUtc is {DailyAtHourUtc}, which is not an hour of the "
                + "day. It is the UTC hour the planner wakes up, from 0 to 23.");
        }
    }
}
