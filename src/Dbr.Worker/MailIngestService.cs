// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Mail;
using Dbr.Infrastructure.Tenancy;

namespace Dbr.Worker;

/// <summary>
/// Wakes up, reads whatever has answered this instance's demands, and files it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scope per reply.</b> The same arrangement as the dispatchers, and for the same
/// reason: the tenant is written once per scope by design, so reusing one across two
/// replies would mean a unit of work spanning two accounts — precisely what the boundary
/// is for. It also means one reply's problem is one reply's problem.
/// </para>
/// <para>
/// <b>Resolve, then file, then acknowledge, in that order.</b> A message is acknowledged
/// only once something has been done with it, so a process that dies mid-pass reads the
/// same message again rather than losing a company's only answer. Filing refuses a
/// duplicate, which is what makes reading it twice harmless.
/// </para>
/// <para>
/// <b>A message that resolves to nothing is acknowledged too.</b> A mailbox that demands
/// go out from receives everything anybody sends to it, and most of it answers nothing;
/// leaving those unread would mean every pass forever re-reading the same accumulating
/// pile to reach whatever is new behind it. What is lost is the ability to notice later
/// that a reply was misfiled — which is why the count of unresolved messages is logged,
/// and why a deployment seeing that number climb has something to look at.
/// </para>
/// </remarks>
public sealed class MailIngestService(
    IInboundMailSource source,
    IAnsweredDemandDirectory directory,
    IJobMailboxes mailboxes,
    IServiceScopeFactory scopes,
    InboundMailOptions options,
    ILogger<MailIngestService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));

        // A pass before the first tick, so a process restarted with answers already waiting
        // reads them rather than idling out the interval first.
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
                // service, and a worker that has quietly stopped reading replies looks
                // exactly like companies that have stopped answering.
                logger.LogError(
                    exception,
                    "A mail ingest pass failed. The next one runs in {Seconds}s.",
                    options.PollSeconds);
            }
        }
        while (await ticks.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One pass: read what has arrived and file whatever answers a demand.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive a pass without a host around it, the same arrangement the
    /// dispatchers use. There is nothing else to drive: the timer above is the whole of
    /// what this class adds.
    /// </remarks>
    /// <returns>How many replies were filed against an attempt.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var arrived = await source.FetchAsync(options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (arrived.Count == 0)
        {
            return 0;
        }

        var filed = 0;
        var unresolved = 0;

        foreach (var message in arrived)
        {
            try
            {
                var match = await directory
                    .ResolveAsync(ReplyCorrelation.Evidence(message, mailboxes), cancellationToken)
                    .ConfigureAwait(false);

                if (match is null)
                {
                    unresolved++;
                }
                else if (await FileAsync(message, match, cancellationToken).ConfigureAwait(false)
                    is ReplyFiling.Filed)
                {
                    filed++;

                    logger.LogInformation(
                        "A reply to attempt {JobId} on request {RequestId} was filed, "
                        + "resolved by {MatchedBy}.",
                        match.Demand.JobId,
                        match.Demand.RemovalRequestId,
                        match.MatchedBy);
                }

                await source.AcknowledgeAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One message's problem is one message's problem, and an unacknowledged
                // message is read again next pass — which is the right way for this to
                // fail. The reference is the mailbox's own, and names no account.
                logger.LogError(
                    exception,
                    "Reading the message at {SourceRef} failed; it stays unread and the rest "
                    + "of the batch continues.",
                    message.SourceRef);
            }
        }

        logger.LogInformation(
            "Mail ingest pass finished: {Filed} of {Arrived} messages answered a demand, "
            + "{Unresolved} answered none.",
            filed,
            arrived.Count,
            unresolved);

        return filed;
    }

    private async Task<ReplyFiling> FileAsync(
        InboundMessage message,
        AnsweredDemandMatch match,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        scope.ServiceProvider
            .GetRequiredService<TenantContext>()
            .SetTenant(match.Demand.TenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IReplyFiler>()
            .FileAsync(message, match, cancellationToken)
            .ConfigureAwait(false);
    }
}
