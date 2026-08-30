// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>How an attempt to start a queued scan ended.</summary>
public enum ScanDispatchOutcome
{
    /// <summary>The run is under way, and its legs are in their companies' lanes.</summary>
    Started,

    /// <summary>
    /// There was nothing to claim.
    /// </summary>
    /// <remarks>
    /// One outcome for a scan that does not exist, one belonging to another account, and
    /// one that had already been picked up. They are the same thing to whoever is trying
    /// to start it — and the third is not an error at all: two dispatchers racing for the
    /// same run is the case the claim exists to settle, and the loser has nothing to do.
    /// </remarks>
    NotClaimable,

    /// <summary>
    /// The run was claimed and there was nobody to ask.
    /// </summary>
    /// <remarks>
    /// A catalog with no active companies in it, or a scan narrowed to companies that have
    /// since been deactivated. The run is over rather than stuck, and it is over
    /// successfully: every broker in scope was reached, and there were none.
    /// </remarks>
    NothingInScope,
}

/// <param name="Planned">Legs put into a company's lane.</param>
/// <param name="Unplannable">
/// Legs that were over before they were sent — no search in this build for that company,
/// or a grant that could not be minted. Recorded as rows rather than skipped, so that a
/// scan of forty companies able to search four says so.
/// </param>
public sealed record ScanDispatchResult(
    ScanDispatchOutcome Outcome,
    int Planned,
    int Unplannable)
{
    public static ScanDispatchResult NotClaimable() =>
        new(ScanDispatchOutcome.NotClaimable, 0, 0);
}

/// <summary>
/// Turns a queued scan into work sitting in each company's lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs where the work is planned, which is not where the keys are.</b> Everything
/// this does is the core store — claim a run, resolve which companies it covers, write a
/// row of random bytes per leg, hand a message to a queue. None of it decrypts, which is
/// why the process that also drives browsers against broker sites can do it.
/// </para>
/// <para>
/// <b>It mints a grant per leg, and that is the first thing in this system to do so.</b>
/// A leg carries permission to see exactly the groups of an identity its search declared
/// it needs, for a window sized to the work rather than to the depth of the lane it is
/// waiting in. What travels the queue is that permission and four ids; the identity itself
/// stays encrypted until a worker presents the grant to the process that holds the keys.
/// </para>
/// <para>
/// <b>One scan at a time, and it takes an id rather than finding its own work.</b> Whose
/// scans exist is a question that reaches past the tenant boundary and is answered by the
/// one narrow thing allowed to ask it; this acts for one account, inside the boundary,
/// like every other write.
/// </para>
/// </remarks>
public interface IScanDispatcher
{
    /// <summary>Claims one queued scan and puts its legs into their lanes.</summary>
    Task<ScanDispatchResult> DispatchAsync(Guid scanId, CancellationToken cancellationToken);
}
