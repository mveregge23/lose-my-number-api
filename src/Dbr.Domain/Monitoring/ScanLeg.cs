// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// One broker's share of one scan, and how it went.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <see cref="ScanBroker"/>, which is a different question.</b> That
/// table is the scope the tenant asked for — which companies, if they narrowed it — and a
/// scan of the whole catalog has no rows in it at all. This one is the work: a row per
/// company the run actually planned to ask, written when the plan is made, whatever the
/// tenant did or did not narrow. Collapsing the two would make "the whole catalog" and
/// "nothing was planned" the same absence.
/// </para>
/// <para>
/// <b>Without it a scan could never be said to have finished.</b> The legs run
/// independently, each in its own lane, each whenever that company may next be spoken to
/// — so nothing that dispatches them is still around when the last one lands. What decides
/// that a run is over is that no leg is unfinished, which is a question about rows.
/// </para>
/// <para>
/// <b>It holds counts, not findings.</b> How many candidates a broker offered and how many
/// were worth showing anybody are numbers about a company, and they are what tells an
/// operator that the bar is set wrong — a broker offering forty candidates a scan and
/// recording none is a search matching too loosely or a floor set too high, and neither is
/// visible from the exposures alone, because the ones that did not clear were never
/// written.
/// </para>
/// </remarks>
public class ScanLeg : ITenantScoped
{
    /// <summary>The account the run belongs to.</summary>
    /// <remarks>
    /// Carried on the row rather than reached through the scan, like every tenant-scoped
    /// child here: it is what the foreign key over the pair ties to the parent, and what
    /// the policy underneath can actually enforce.
    /// </remarks>
    public Guid TenantId { get; init; }

    public required Guid ScanId { get; init; }

    public required Guid BrokerId { get; init; }

    /// <summary>Which try this is, from one.</summary>
    /// <remarks>
    /// Here rather than derived from a count of rows, because there is one row per leg and
    /// a retry rewrites it. A retry is a fresh grant and a fresh dispatch — the old one is
    /// single-use and spent — so what the number records is how many times this company
    /// has been asked for this run, which is what a pacing decision would want to read.
    /// </remarks>
    public required int AttemptNumber { get; set; }

    /// <summary>When the fan-out decided to ask this company.</summary>
    public DateTimeOffset PlannedAt { get; init; }

    /// <summary>When a worker took it out of the lane, or <see langword="null"/> while it waits.</summary>
    /// <remarks>
    /// The gap between this and <see cref="PlannedAt"/> is how long the company's lane was
    /// busy, which is the number that says whether pacing is the reason a scan is slow.
    /// A single column doing both jobs would report every queued leg as having started.
    /// </remarks>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When it ended, or <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// How it ended, or <see langword="null"/> while it has not.
    /// </summary>
    /// <remarks>
    /// Null is the whole of "unfinished", which is what makes the question a scan has to
    /// ask — is every leg done — a query for the rows where this is absent rather than a
    /// list of the values that count as over.
    /// </remarks>
    public ScanLegOutcome? Outcome { get; set; }

    /// <summary>
    /// What actually happened, for whoever reads the row afterwards.
    /// </summary>
    /// <remarks>
    /// The search's own account of a failure, or the planner's. <b>Never the identity being
    /// searched for and never the page's content</b> — a status line, a selector that did
    /// not match, the name of a timeout. It is held to the same rule as a log line because
    /// it is read for the same reason and lives longer.
    /// </remarks>
    public string? Detail { get; set; }

    /// <summary>How many listings the broker offered as possibly this person.</summary>
    public int CandidatesFound { get; set; }

    /// <summary>
    /// How many of those cleared the bar and became findings somebody is shown.
    /// </summary>
    /// <remarks>
    /// The difference between this and <see cref="CandidatesFound"/> is the only surviving
    /// trace of the candidates that did not clear, and it is deliberately the only one: a
    /// row for each would be a durable record of a weak claim about somebody that nothing
    /// will ever act on.
    /// </remarks>
    public int CandidatesRecorded { get; set; }
}
