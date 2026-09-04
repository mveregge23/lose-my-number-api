// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// How often a queued demand gets picked up, and how many at a time.
/// </summary>
/// <remarks>
/// Polling, for the reason the scan dispatcher polls: a demand is written by a request
/// handler inside a database transaction, and a notification sent alongside it is either
/// inside that transaction, where it cannot be, or outside it, where a crash loses the
/// work. The table is the record, so asking the table is the arrangement that cannot lose a
/// demand — and one picked up a few seconds late then waits in a company's lane anyway.
/// </remarks>
public sealed class RemovalDispatchOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "RemovalDispatch";

    /// <summary>Whether this process sends queued demands at all.</summary>
    /// <remarks>
    /// On by default. A demand somebody opened and that nothing ever sends is the failure
    /// this exists to remove, so leaving it out should take a deliberate act rather than a
    /// missing setting.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait between passes.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>
    /// How many demands one pass takes.
    /// </summary>
    /// <remarks>
    /// A batch rather than a drain, so a backlog makes progress on each pass and leaves the
    /// rest. Smaller than the scan side's for a reason: a scan fans out to a whole catalog
    /// and its cost is in the fan-out, while each of these is one message into one company's
    /// lane — so a large batch here is a large number of lanes opened at once.
    /// </remarks>
    public int BatchSize { get; set; } = 10;

    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (PollSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:PollSeconds must be at least 1, and is {PollSeconds}. A zero or "
                + "negative interval is a loop that queries the database as fast as it can "
                + "answer.");
        }

        if (BatchSize < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BatchSize must be at least 1, and is {BatchSize}. A batch of "
                + "none is a dispatcher that runs and sends nothing, which looks exactly like "
                + "one that is broken.");
        }
    }
}
