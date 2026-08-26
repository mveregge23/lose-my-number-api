// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Removals;

/// <summary>What became of one attempt.</summary>
/// <remarks>
/// <para>
/// A job is a single attempt at a request; the request carries the lifecycle. Four states
/// rather than the request's nine, because a job has nothing to wait on a broker for and
/// nothing a person can clear — those are things that happen to the <i>request</i>, and a
/// job that produced one of them has already finished.
/// </para>
/// <para>
/// So <see cref="Succeeded"/> here means the attempt ran and reported something, not that
/// the listing is gone. Whether it is gone is a question only a verification scan answers,
/// and it is recorded on the request.
/// </para>
/// </remarks>
public enum RemovalJobStatus
{
    /// <summary>Scheduled, not yet picked up.</summary>
    Pending,

    /// <summary>A worker has it.</summary>
    Running,

    /// <summary>The attempt ran and reported an outcome.</summary>
    Succeeded,

    /// <summary>The attempt did not complete.</summary>
    Failed,
}
