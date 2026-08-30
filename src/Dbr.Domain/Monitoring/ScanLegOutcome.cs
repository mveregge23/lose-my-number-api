// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>
/// How one broker's leg of a scan ended.
/// </summary>
/// <remarks>
/// <para>
/// One vocabulary covering both halves of a leg's life, because a leg can end before a
/// search ever runs. <see cref="NoSearchAvailable"/> and <see cref="ReleaseRefused"/>
/// happen while the work is being planned or picked up; the rest are the answers a search
/// gives, carried across unchanged so that the reason a broker went unanswered reads the
/// same whether it was the search that said so or the plumbing in front of it. Two
/// enumerations would have meant a row with two nullable columns, exactly one of which is
/// ever set.
/// </para>
/// <para>
/// <b>None of these is "still going".</b> A leg that has not ended has no outcome at all,
/// which is what makes "is this scan finished" a query for the legs with none rather than
/// a list of the values that count as over.
/// </para>
/// </remarks>
public enum ScanLegOutcome
{
    /// <summary>The broker answered, and it holds listings that might be this person.</summary>
    /// <remarks>
    /// About the answer, not about what was kept. A leg that found candidates and recorded
    /// none of them because every one fell below the bar is still this — the broker was
    /// reached and it said something, and the counts on the row are where "and none of it
    /// was worth showing anybody" is recorded.
    /// </remarks>
    Found,

    /// <summary>The broker answered, and holds nothing about this person.</summary>
    NothingFound,

    /// <summary>
    /// Nothing in this build knows how to search this company.
    /// </summary>
    /// <remarks>
    /// The ordinary state of most of the catalog while searches are still being written,
    /// and it is deliberately not a failure of the broker. Recorded rather than skipped
    /// so that a scan covering forty companies and able to search four says so.
    /// </remarks>
    NoSearchAvailable,

    /// <summary>The grant this leg carried would not open.</summary>
    /// <remarks>
    /// Expired while the lane was busy, already spent, or minted against a run that has
    /// since stopped. One outcome for all of them, because the edge gives one answer to a
    /// caller holding a token — and because the response is the same in every case: this
    /// leg is over and a retry needs a fresh grant rather than another go at this one.
    /// </remarks>
    ReleaseRefused,

    /// <summary>The search answered in a way it was not entitled to.</summary>
    /// <remarks>
    /// A finding claiming a field the search was never given, two findings pointing at one
    /// listing, a result that reported findings and listed none. A bug in the search rather
    /// than anything about the company, so it is recorded against the leg and nothing is
    /// written from the answer.
    /// </remarks>
    ContractBroken,

    /// <summary>A timeout, a reset, a 5xx.</summary>
    Transient,

    /// <summary>The broker throttled this instance and said so.</summary>
    RateLimited,

    /// <summary>The page was reachable and no longer looks like what the search expects.</summary>
    PageShapeChanged,

    /// <summary>The broker refused to serve this instance at all.</summary>
    Blocked,

    /// <summary>The search cannot do what this attempt asked of it.</summary>
    Unsupported,

    /// <summary>The search threw.</summary>
    /// <remarks>
    /// Distinct from every failure a search can report, because a search that throws did
    /// not decide anything — it is a bug, and reporting it as a transient network problem
    /// would leave it looking like a broker having a bad day for as long as nobody read
    /// the log.
    /// </remarks>
    Faulted,
}

/// <summary>Reading an outcome without enumerating the ways a leg can go wrong.</summary>
public static class ScanLegOutcomes
{
    /// <summary>
    /// Whether the broker was actually reached and its answer recorded.
    /// </summary>
    /// <remarks>
    /// The one place the eleven outcomes collapse into the two that decide whether a run
    /// covered what it set out to cover. Written once here rather than as a list repeated
    /// at each call site, because the list is what grows when a new way to fail is added
    /// and a copy of it is what silently stops growing.
    /// </remarks>
    public static bool IsAnswer(ScanLegOutcome outcome) =>
        outcome is ScanLegOutcome.Found or ScanLegOutcome.NothingFound;
}
