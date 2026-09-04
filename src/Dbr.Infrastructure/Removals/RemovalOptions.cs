// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// How many times one demand may be attempted before it is given up on.
/// </summary>
/// <remarks>
/// Configuration rather than a constant because the right number is a judgement about a
/// deployment, not about the domain: an instance asking four hundred companies on behalf of
/// thousands of people has a different tolerance for hammering a flaky one than a
/// self-hoster asking on behalf of themselves.
/// </remarks>
public sealed class RemovalOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Removals";

    /// <summary>
    /// The most attempts one demand gets.
    /// </summary>
    /// <remarks>
    /// Three, which is the same figure the catalog uses for the failures that open a
    /// breaker or flag an entry for review, and chosen to agree with them rather than
    /// independently: a demand that has failed three times and a company that has changed
    /// its form three times are usually the same event seen from two directions, and two
    /// different budgets would have one of them notice first for no reason anybody could
    /// explain afterwards.
    /// </remarks>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// How long after a failed attempt the next one becomes due.
    /// </summary>
    /// <remarks>
    /// A flat wait rather than a growing one, and that is a deliberate simplification worth
    /// naming. The usual argument for backoff is to stop a client hammering a server it is
    /// already struggling to reach — and here that job belongs to the company's lane, which
    /// paces every attempt against it regardless of how many have failed. What this wait is
    /// actually for is narrower: to stop a demand that failed for a reason time might fix
    /// being retried in the same second. Growing it would spread retries out per demand
    /// while the lane goes on admitting the same number per minute, which changes who waits
    /// rather than how hard the company is pushed.
    /// </remarks>
    public int RetryBackoffSeconds { get; set; } = 900;

    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (RetryBackoffSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryBackoffSeconds must be at least 1, and is "
                + $"{RetryBackoffSeconds}. A demand retried in the same instant it failed is "
                + "one that spends its whole budget before anything has had time to change.");
        }

        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaxAttempts must be at least 1, and is {MaxAttempts}. A demand "
                + "that may never be attempted is a request this service accepts and will "
                + "never act on, which is worse than refusing it.");
        }
    }
}
