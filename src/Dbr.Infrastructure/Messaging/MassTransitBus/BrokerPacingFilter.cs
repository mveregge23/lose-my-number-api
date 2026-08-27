// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using MassTransit;

namespace Dbr.Infrastructure.Messaging.MassTransitBus;

/// <summary>
/// Puts a lane's pacing into the consume pipeline.
/// </summary>
/// <remarks>
/// Deliberately almost nothing. The rule about how far apart a broker's jobs may start
/// lives in <see cref="BrokerPacer"/>, which knows about neither queues nor messages; this
/// is the adapter that calls it at the right moment. Replacing the transport means writing
/// the equivalent three lines for whatever replaces it, not reimplementing the pacing.
/// </remarks>
public sealed class BrokerPacingFilter(BrokerPacer pacer) : IFilter<ConsumeContext>, IDisposable
{
    public void Probe(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = context.CreateFilterScope("brokerPacing");
        scope.Add("brokerId", pacer.Lane.BrokerId);
        scope.Add("minDelayMs", pacer.Lane.MinDelay.TotalMilliseconds);
        scope.Add("maxConcurrency", pacer.Lane.MaxConcurrency);
    }

    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await pacer.WaitForTurnAsync(context.CancellationToken).ConfigureAwait(false);

        await next.Send(context).ConfigureAwait(false);
    }

    public void Dispose() => pacer.Dispose();
}
