// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Tenancy;
using Quartz;

namespace Dbr.Worker;

/// <summary>
/// Once a day: work out whose monthly scan falls today, and queue it for each of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The job is daily; the scan is monthly.</b> Waking every day and asking who is due
/// is what lets each account keep its own day of the month without the scheduler holding
/// any state about them. Nothing is stored about when anybody was last scanned — the day
/// falls out of the account id, and the row already written for today is what stops it
/// happening twice.
/// </para>
/// <para>
/// <b>One scope per account.</b> The tenant is written once per scope by design, so
/// reusing one would mean a unit of work spanning every account on the instance —
/// precisely the thing the boundary is for. A new scope per account also means one
/// account's failure is one account's failure.
/// </para>
/// <para>
/// <b>It logs counts and ids and nothing else.</b> An account id is an id; how many scans
/// were queued for it is a number. Neither is a name, which is the rule every log line in
/// this system is held to.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ScheduledScanJob(
    IAccountDirectory accounts,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ScheduledScanJob> logger)
    : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await PlanAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The run itself, without Quartz around it.
    /// </summary>
    /// <remarks>
    /// Separated so a test can drive a whole day's planning against a real database and a
    /// clock it controls. The alternative is standing up an <see cref="IJobExecutionContext"/>,
    /// which would mean a great deal of scaffolding to reach the one line that reads a
    /// cancellation token off it.
    /// </remarks>
    public async Task PlanAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var everyAccount = await accounts.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false);
        var due = everyAccount.Where(id => ScanSchedule.IsDue(id, today)).ToList();

        logger.LogInformation(
            "Scheduled scan planning for {Date}: {DueCount} of {AccountCount} accounts due.",
            today,
            due.Count,
            everyAccount.Count);

        var queued = 0;
        var refused = 0;
        var failed = 0;

        foreach (var accountId in due)
        {
            try
            {
                var run = await RunForAsync(accountId, cancellationToken).ConfigureAwait(false);

                queued += run.Queued;

                if (run.ConsentMissing)
                {
                    refused++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One account's problem is one account's problem. Letting it end the run
                // would mean everybody sorted after them silently misses a month, and the
                // symptom — some accounts scanned, some not — is nearly invisible.
                failed++;
                logger.LogError(
                    exception,
                    "Scheduled scan planning failed for account {AccountId}; continuing with the rest.",
                    accountId);
            }
        }

        logger.LogInformation(
            "Scheduled scan planning for {Date} finished: {Queued} scans queued, {Refused} accounts "
            + "have not permitted scanning, {Failed} failed.",
            today,
            queued,
            refused,
            failed);
    }

    private async Task<ScheduledScanRun> RunForAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(accountId);

        return await scope.ServiceProvider
            .GetRequiredService<IScheduledScanRunner>()
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
