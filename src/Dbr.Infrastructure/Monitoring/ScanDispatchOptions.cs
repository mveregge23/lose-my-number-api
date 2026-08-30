// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// How often a queued scan gets picked up, and how many at a time.
/// </summary>
/// <remarks>
/// <para>
/// Polling, and it is worth saying why rather than leaving it to look like an oversight. A
/// scan is written by a request handler or by the scheduler, in a database transaction; a
/// notification sent alongside it is either inside that transaction, where it cannot be,
/// or outside it, where a crash loses the run entirely. The table is the record, so asking
/// the table is the arrangement that cannot lose work — and a run picked up a few seconds
/// late is a run that then waits in a broker's lane anyway.
/// </para>
/// <para>
/// The costs of polling that usually make it a poor choice do not apply at this shape: one
/// query per interval for the whole instance, answered from an index, and it returns
/// nothing at all most of the time.
/// </para>
/// </remarks>
public sealed class ScanDispatchOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "ScanDispatch";

    /// <summary>Whether this process starts queued scans at all.</summary>
    /// <remarks>
    /// On by default, unlike the recurring schedule. A scan somebody asked for and that
    /// nothing ever starts is the failure this whole story exists to remove, so it should
    /// take a deliberate act to reintroduce it rather than a missing setting.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait between passes.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>
    /// How many runs one pass takes.
    /// </summary>
    /// <remarks>
    /// A batch rather than a drain. A backlog should make progress on each pass and leave
    /// the rest, so that one wake-up after an outage does not mint a grant for every leg of
    /// every waiting run at once — every one of those grants starts expiring immediately,
    /// while the lanes they are queued in still only move at the pace each company allows.
    /// </remarks>
    public int BatchSize { get; set; } = 20;

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
                + "none is a dispatcher that runs and starts nothing, which looks exactly like "
                + "one that is broken.");
        }
    }
}
