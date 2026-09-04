// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Removals;
using Dbr.Infrastructure.Removals;
using Dbr.Infrastructure.Tenancy;

namespace Dbr.Worker;

/// <summary>
/// Wakes up, finds the demands nobody has sent, and sends them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scope per demand.</b> The tenant is written once per scope by design, so reusing
/// one would mean a unit of work spanning several accounts — precisely what the boundary is
/// for. It also means one account's failure is one account's failure: a demand that throws
/// is logged and the next is still sent.
/// </para>
/// <para>
/// <b>It finds the work and does not do it.</b> Asking which demands are waiting reaches
/// past the tenant boundary and is answered by the one narrow thing allowed to ask;
/// everything with a consequence happens inside a scope acting for one account, through the
/// ordinary path. The privileged step is one statement wide, as it is for the scan
/// dispatcher and the scheduler.
/// </para>
/// <para>
/// <b>It logs counts and ids and nothing else.</b> An account id is an id and a request id
/// is an id; how many were sent is a number. None of them is a name.
/// </para>
/// </remarks>
public sealed class RemovalDispatchService(
    IQueuedRemovalDirectory queued,
    IServiceScopeFactory scopes,
    RemovalDispatchOptions options,
    ILogger<RemovalDispatchService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));

        // A pass before the first tick, so a process restarted with demands already waiting
        // gets on with them rather than idling out the interval first.
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A pass that failed is a pass. Letting it escape would end the hosted
                // service, and a worker whose dispatcher has quietly stopped looks exactly
                // like one where nobody has opened a demand.
                logger.LogError(
                    exception,
                    "A removal dispatch pass failed. The next one runs in {Seconds}s.",
                    options.PollSeconds);
            }
        }
        while (await ticks.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One pass: take a batch of waiting demands and send each of them.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive a pass against a real database without a host around it,
    /// the same arrangement the scan dispatcher and the scheduled job use. There is nothing
    /// else to drive: the timer above is the whole of what this class adds.
    /// </remarks>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var waiting = await queued
            .ListQueuedAsync(options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (waiting.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var demand in waiting)
        {
            try
            {
                var result = await SendAsync(demand, cancellationToken).ConfigureAwait(false);

                if (result.Outcome is RemovalDispatchOutcome.Dispatched)
                {
                    sent++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One demand's problem is one demand's problem. The request stays as it was
                // — still queued if the claim never landed, or submitted with an attempt
                // that nothing will pick up, which the attempt row records.
                logger.LogError(
                    exception,
                    "Sending removal request {RequestId} for account {AccountId} failed; "
                    + "continuing with the rest of the batch.",
                    demand.RemovalRequestId,
                    demand.TenantId);
            }
        }

        logger.LogInformation(
            "Removal dispatch pass finished: {Sent} of {Waiting} demands sent.",
            sent,
            waiting.Count);

        return sent;
    }

    private async Task<RemovalDispatchResult> SendAsync(
        QueuedRemoval demand,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        scope.ServiceProvider
            .GetRequiredService<TenantContext>()
            .SetTenant(demand.TenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IRemovalDispatcher>()
            .DispatchAsync(demand.RemovalRequestId, cancellationToken)
            .ConfigureAwait(false);
    }
}
