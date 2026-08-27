// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Dbr.Domain.Messaging;

namespace Dbr.Infrastructure.Messaging;

/// <summary>
/// Holds one broker's lane to the gap its catalog row asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A gap, not a rate.</b> §10.1 suggests a token-bucket limiter from
/// <c>System.Threading.RateLimiting</c>, and that was tried first. It does not give this
/// guarantee. A bucket of one token refilled every <c>minDelayMs</c> enforces an average
/// rate, and its replenishment timer runs on a fixed period from when the limiter was
/// built rather than from when a token was last spent — so a job taking the token just
/// before a tick is followed by one taking the new token immediately after it. Measured,
/// the first two jobs in a lane configured for 150ms started 22ms apart. The catalog field
/// is the delay <i>between</i> jobs, so this tracks when the next one may start.
/// </para>
/// <para>
/// Knows nothing about queues or messages, which is why it can be tested by calling it in
/// a loop and looking at a clock rather than by standing up a bus. The transport-specific
/// part is a three-line filter that calls this.
/// </para>
/// <para>
/// Nothing is ever turned away. A refused job would have to become a retry, and a retry is
/// another message; pacing that produced more traffic under pressure would be worse than
/// none. The queue is the real backpressure and is allowed to grow.
/// </para>
/// </remarks>
public sealed class BrokerPacer(BrokerLane lane) : IDisposable
{
    /// <summary>Monotonic, so a clock adjustment cannot make a lane sprint or stall.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private TimeSpan _nextStart = TimeSpan.Zero;

    /// <summary>The lane being paced.</summary>
    public BrokerLane Lane { get; } = lane ?? throw new ArgumentNullException(nameof(lane));

    /// <summary>
    /// Returns when this lane may start another job, having reserved that slot.
    /// </summary>
    /// <remarks>
    /// The wait is serialized so starts are ordered, and the gate is released as soon as
    /// the slot is taken rather than held for the work: this meters when a job may
    /// <i>begin</i>. Holding it for the duration would throttle a slow broker twice — once
    /// by its own latency and again by this — which is the opposite of the point. How many
    /// may run at once is a separate control.
    /// </remarks>
    public async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_nextStart - _clock.Elapsed is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            // Measured from the actual start rather than the scheduled one, so a lane that
            // fell behind does not then try to catch up by starting jobs early.
            _nextStart = _clock.Elapsed + Lane.MinDelay;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
