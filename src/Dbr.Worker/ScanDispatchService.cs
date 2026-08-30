// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Tenancy;

namespace Dbr.Worker;

/// <summary>
/// Wakes up, finds the runs nobody has started, and starts them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scope per run.</b> The tenant is written once per scope by design, so reusing one
/// would mean a unit of work spanning several accounts — precisely the thing the boundary
/// is for. It also means one account's failure is one account's failure: a run that throws
/// is logged and the next is still started.
/// </para>
/// <para>
/// <b>It finds the work and does not do it.</b> Asking which runs are waiting reaches past
/// the tenant boundary and is answered by the one narrow thing allowed to ask; everything
/// with a consequence happens inside a scope acting for one account, through the ordinary
/// path. The privileged step is one statement wide, as it is for the scheduler.
/// </para>
/// <para>
/// <b>It logs counts and ids and nothing else.</b> An account id is an id and a scan id is
/// an id; how many legs were queued is a number. None of them is a name.
/// </para>
/// </remarks>
public sealed class ScanDispatchService(
    IQueuedScanDirectory queued,
    IServiceScopeFactory scopes,
    ScanDispatchOptions options,
    ILogger<ScanDispatchService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));

        // A pass before the first tick, so a process restarted with work already waiting
        // gets on with it rather than idling out the interval first.
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
                // like one where nobody has asked for a scan — which is to say, like
                // nothing at all.
                logger.LogError(
                    exception,
                    "A scan dispatch pass failed. The next one runs in {Seconds}s.",
                    options.PollSeconds);
            }
        }
        while (await ticks.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One pass: take a batch of waiting runs and start each of them.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive a pass against a real database without a host around it,
    /// the same arrangement the scheduled job uses. There is nothing else to drive: the
    /// timer above is the whole of what this class adds.
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

        var started = 0;

        foreach (var scan in waiting)
        {
            try
            {
                var result = await StartAsync(scan, cancellationToken).ConfigureAwait(false);

                if (result.Outcome is not ScanDispatchOutcome.NotClaimable)
                {
                    started++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One run's problem is one run's problem. The scan stays as it was — either
                // still queued, if the claim never landed, or under way with fewer legs than
                // it should have, which the leg rows record.
                logger.LogError(
                    exception,
                    "Starting scan {ScanId} for account {AccountId} failed; continuing with the "
                    + "rest of the batch.",
                    scan.ScanId,
                    scan.TenantId);
            }
        }

        logger.LogInformation(
            "Scan dispatch pass finished: {Started} of {Waiting} runs started.",
            started,
            waiting.Count);

        return started;
    }

    private async Task<ScanDispatchResult> StartAsync(
        QueuedScan scan,
        CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(scan.TenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IScanDispatcher>()
            .DispatchAsync(scan.ScanId, cancellationToken)
            .ConfigureAwait(false);
    }
}
