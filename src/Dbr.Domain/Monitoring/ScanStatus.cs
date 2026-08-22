// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>How far a scan has got.</summary>
/// <remarks>
/// <para>
/// Four states rather than the removal lifecycle's nine. A scan asks brokers what they
/// hold and records what came back; it has nothing to wait on a broker to decide, no
/// deadline it can miss, and nothing a person has to clear. Borrowing the removal states
/// would put transitions on this table that nothing can ever perform.
/// </para>
/// <para>
/// <see cref="Failed"/> is about the run, not about the findings. A scan that reached
/// every broker and found nothing succeeded — an empty result is an answer.
/// </para>
/// </remarks>
public enum ScanStatus
{
    /// <summary>Accepted and waiting for a worker to pick it up.</summary>
    Queued,

    /// <summary>A worker is working through the brokers.</summary>
    Running,

    /// <summary>Every broker in scope was reached and its answer recorded.</summary>
    Completed,

    /// <summary>The run stopped without covering its brokers.</summary>
    Failed,
}
