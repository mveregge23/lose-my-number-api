// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Dbr.Domain.Messaging;
using MassTransit;

namespace Dbr.Infrastructure.Messaging;

/// <summary>
/// Holds one broker's lane to the pace its catalog row allows.
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
/// is the delay <i>between</i> jobs, so this tracks when the next one may start and waits
/// until then.
/// </para>
/// <para>
/// The wait is serialized, so starts are ordered and each one sets the next lane's
/// earliest start before releasing. The gate is released before the consumer runs, not
/// after: this meters when work may <i>begin</i>. Holding it for the duration would make a
/// slow broker throttle itself twice — once by its own latency and again by this — which
/// is the opposite of what pacing is for. How many may run at once is the endpoint's
/// concurrency limit, and a separate question.
/// </para>
/// <para>
/// Nothing is ever turned away. A refused job would have to become a retry, and a retry is
/// another message; pacing that produced more traffic under pressure would be worse than
/// none. The real backpressure is the queue, which is allowed to grow — while a broker is
/// slow, work accumulates rather than being dropped.
/// </para>
/// </remarks>
public sealed class BrokerPacingFilter : IFilter<ConsumeContext>, IDisposable
{
    private readonly BrokerLane _lane;

    /// <summary>Monotonic, so a clock adjustment cannot make a lane sprint or stall.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private TimeSpan _nextStart = TimeSpan.Zero;

    public BrokerPacingFilter(BrokerLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);

        _lane = lane;
    }

    public void Probe(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = context.CreateFilterScope("brokerPacing");
        scope.Add("brokerId", _lane.BrokerId);
        scope.Add("minDelayMs", _lane.MinDelay.TotalMilliseconds);
        scope.Add("maxConcurrency", _lane.MaxConcurrency);
    }

    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await WaitForTurnAsync(context.CancellationToken).ConfigureAwait(false);

        await next.Send(context).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_nextStart - _clock.Elapsed is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            // Measured from the actual start rather than from the scheduled one, so a lane
            // that fell behind does not then try to catch up by starting jobs early.
            _nextStart = _clock.Elapsed + _lane.MinDelay;
        }
        finally
        {
            _gate.Release();
        }
    }
}
