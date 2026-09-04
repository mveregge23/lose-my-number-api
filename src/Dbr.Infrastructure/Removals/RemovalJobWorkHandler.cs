// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Messaging;
using Dbr.Domain.Profiles;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// Makes one demand of one company, and writes down where that leaves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not throw when a company fails, for the reason the scan handler does not.</b>
/// Throwing hands the message back to the transport, which redelivers it — and a redelivered
/// demand is a company asked twice in one person's name. An attempt that could not be
/// completed is therefore a finished attempt with a reason on it, and trying again means a
/// fresh attempt that the dispatcher claims in the ordinary way. Exceptions escape only
/// where they mean what the handler contract says: the work did not happen at all.
/// </para>
/// <para>
/// <b>What the demand becomes is decided by a pure function and applied here.</b> The
/// mapping from a connector's answer to the lifecycle is the substance of dispatching, and
/// keeping it out of this class is what lets every branch of it be checked without a
/// database. This method's own job is the parts that need one: finding the attempt,
/// refusing a repeat delivery, and moving two rows in step.
/// </para>
/// <para>
/// <b>There is no identity here, and that is the story's own finding rather than an
/// oversight.</b> A connector needs one — it is filling in a form or composing a message
/// naming a person — and there is no way to obtain one: the vault's scoped release is keyed
/// to a scan by its schema and its definer function, and widening it to an attempt is its
/// own story with its own audit-trail obligations. So a connector runs against an identity
/// that released nothing, which the contract permits and which a real connector answers by
/// saying it cannot work. That answer is recorded rather than special-cased, so the day the
/// release exists the only thing that changes is what this hands over.
/// </para>
/// </remarks>
public sealed class RemovalJobWorkHandler(
    DbrDbContext core,
    TenantContext tenantContext,
    IBrokerConnectorRegistry connectors,
    IOptions<RemovalOptions> options,
    TimeProvider clock,
    ILogger<RemovalJobWorkHandler> logger)
    : IBrokerWorkHandler<RemovalJobWork>
{
    private readonly RemovalOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task HandleAsync(RemovalJobWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // The scope acts for this account from here on, established from the message: both
        // rows this touches are tenant-scoped and neither can be read without one.
        tenantContext.SetTenant(work.TenantId);

        var job = await core.Set<RemovalJob>()
            .FirstOrDefaultAsync(row => row.Id == work.RemovalJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            // Nothing to record against. A warning rather than an exception: redelivering
            // this message would produce the same nothing.
            logger.LogWarning(
                "Removal job {JobId} does not exist for this account, so its work has nowhere "
                + "to be recorded. Discarding it.",
                work.RemovalJobId);

            return;
        }

        if (job.Status is not RemovalJobStatus.Pending)
        {
            // Already run. A duplicate delivery, which the transport is entitled to produce
            // — and running again would send the same demand to the same company a second
            // time, which is the one outcome the whole lane arrangement exists to avoid.
            logger.LogInformation(
                "Removal job {JobId} has already run and is {Status}. Ignoring a repeat "
                + "delivery.",
                work.RemovalJobId,
                job.Status);

            return;
        }

        var request = await core.Set<RemovalRequest>()
            .FirstOrDefaultAsync(row => row.Id == job.RemovalRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            logger.LogWarning(
                "Removal job {JobId} belongs to a request that is no longer here. Discarding "
                + "its work.",
                work.RemovalJobId);

            return;
        }

        if (request.Status is not RemovalRequestStatus.Submitted)
        {
            // The demand moved on while this attempt sat in the lane — almost always
            // because the person cancelled it. Running the connector anyway would send a
            // company a demand somebody had withdrawn, which is the one outcome a cancel
            // route exists to prevent, and the answer would then have nowhere legal to go.
            job.Status = RemovalJobStatus.Failed;
            job.Detail =
                $"The demand was {RemovalVocabulary.ToWire(request.Status)} before this attempt "
                + "ran, so nothing was sent.";

            await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Removal job {JobId} was not run: request {RequestId} is {Status} rather than "
                + "awaiting dispatch.",
                work.RemovalJobId,
                request.Id,
                request.Status);

            return;
        }

        job.Status = RemovalJobStatus.Running;

        var progress = await RunAsync(work, request, cancellationToken).ConfigureAwait(false);

        Apply(job, request, progress);

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Removal job {JobId} for broker {BrokerId} finished as {JobStatus}; request "
            + "{RequestId} is now {RequestStatus}.",
            work.RemovalJobId,
            work.BrokerId,
            progress.JobStatus,
            request.Id,
            progress.RequestStatus);
    }

    /// <summary>Runs the connector, and says where its answer leaves the demand.</summary>
    private async Task<RemovalProgress> RunAsync(
        RemovalJobWork work,
        RemovalRequest request,
        CancellationToken cancellationToken)
    {
        if (connectors.Find(work.BrokerId) is not { } registration)
        {
            // The build changed between the dispatch and the run. Recorded rather than
            // treated as a fault: a deploy that removed a connector is a deliberate act.
            return Unsupported("This build no longer has a connector for this company.");
        }

        // Read now rather than carried on the message, so a catalog row corrected between
        // dispatch and run is the one that gets used. The lane is named by id for the same
        // reason.
        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == work.BrokerId, cancellationToken)
            .ConfigureAwait(false);

        if (broker is null)
        {
            return Unsupported("This company is no longer in the catalog.");
        }

        var context = new ConnectorContext(
            work.RemovalJobId,
            work.RemovalRequestId,
            new ConnectorTarget(broker.Id, broker.Domain, broker.RemovalMethod),
            Demand(request),

            // Nothing was released, because nothing can be. See the note on this class.
            ProfileIdentityFields.Empty,

            // The listing is in the vault under its own key, so citing it needs the same
            // release the identity does. A demand that cites nothing is an ordinary demand,
            // which is what makes this absence survivable rather than blocking.
            SourceRef: null,

            // No checkpoint. Nothing persists one until the resume path exists, so an
            // attempt that follows a stop starts over rather than picking up.
            Checkpoint: null,
            work.AttemptNumber);

        if (ConnectorContract.Refuse(registration.Connector.Capabilities, context) is { } refusal)
        {
            logger.LogError(
                "Removal job {JobId} was refused before it ran: {Refusal}",
                work.RemovalJobId,
                refusal);

            return Unsupported(refusal);
        }

        ConnectorResult result;

        try
        {
            result = await registration.Connector
                .ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping, which is not this demand's answer. Left to escape so
            // the transport keeps the message; the attempt is still pending, so the same
            // delivery can run it.
            throw;
        }
        catch (Exception exception)
        {
            // A connector that throws decided nothing, and in particular did not decide
            // whether the company was contacted. Treated as not worth repeating: a bug that
            // throws will throw again, and retrying it risks sending a half-made demand
            // twice.
            logger.LogError(
                exception,
                "The connector for broker {BrokerId} threw while running removal job {JobId}.",
                work.BrokerId,
                work.RemovalJobId);

            return new RemovalProgress(
                RemovalJobStatus.Failed,
                RemovalRequestStatus.Failed,
                ConnectorFailureReason.Unsupported,
                RetryWorthwhile: false,
                $"The connector threw {exception.GetType().Name}.");
        }

        if (ConnectorContract.Refuse(registration.Connector.Capabilities, result) is { } broken)
        {
            logger.LogError(
                "The connector for broker {BrokerId} answered removal job {JobId} in a way it "
                + "was not entitled to: {Refusal}",
                work.BrokerId,
                work.RemovalJobId,
                broken);

            return Unsupported(broken);
        }

        return RemovalOutcomes.For(result);
    }

    /// <summary>
    /// Writes the answer onto the attempt and the demand, and schedules another try.
    /// </summary>
    /// <remarks>
    /// <b>The retry lands on the request rather than on a timer.</b> Putting it back to
    /// queued is what makes the dispatcher pick it up again, which keeps one path into a
    /// company's lane rather than two. The next-attempt time is recorded on the attempt that
    /// failed, because backoff is a property of what just happened.
    /// </remarks>
    private void Apply(RemovalJob job, RemovalRequest request, RemovalProgress progress)
    {
        job.Status = progress.JobStatus;
        job.FailureReason = progress.FailureReason;
        job.Detail = Trim(progress.Detail);

        var retryable = progress.RetryWorthwhile && request.Attempt < _options.MaxAttempts;

        if (retryable)
        {
            job.NextRetryAt = clock.GetUtcNow().AddSeconds(_options.RetryBackoffSeconds);
        }

        Move(request, progress.RequestStatus);

        if (retryable && request.Status == RemovalRequestStatus.Failed)
        {
            // Back into the queue, so the dispatcher picks it up on its next pass and there
            // is one path into a company's lane rather than two.
            //
            // Through failed rather than around it, which is not a formality: the lifecycle
            // draws no edge from submitted to queued, and the one it does draw carries the
            // guard about attempts remaining — the very thing just evaluated. A demand that
            // is going to be retried has genuinely failed once, and its history should say
            // so. Nothing left to try leaves it here, and the story that owns deadlines is
            // what later expires it.
            Move(request, RemovalRequestStatus.Queued);
        }
    }

    /// <summary>
    /// Moves a demand, or refuses to.
    /// </summary>
    /// <remarks>
    /// A move the table does not draw is a bug rather than a company having a bad day, and
    /// letting it through would put a demand somewhere the rest of the system does not
    /// believe in. Throwing here happens after the company has already been contacted, which
    /// is unpleasant and still better than the alternative: the attempt row is unsaved, so
    /// the demand stays submitted and a person can see it did not resolve.
    /// </remarks>
    private static void Move(RemovalRequest request, RemovalRequestStatus to)
    {
        if (!RemovalLifecycle.IsAllowed(request.Status, to))
        {
            throw new InvalidOperationException(
                $"A connector's answer would move removal request {request.Id} from "
                + $"{request.Status} to {to}, which the lifecycle does not allow: "
                + RemovalLifecycle.Refuse(request.Status, to));
        }

        request.Status = to;
    }

    /// <summary>What is being demanded, as the connector needs it.</summary>
    /// <remarks>
    /// The citation is not resolved from the catalog here, and the demand carries none as a
    /// result. Reading the regime would mean this process holding an opinion about which
    /// statute governs, which was settled when the request was opened and written onto the
    /// row; serving the code and the URL from it is what the audit-trail story adds, since
    /// that is the point at which what was actually asserted has to be recoverable.
    /// </remarks>
    private static ConnectorDemand Demand(RemovalRequest request) =>
        new(
            request.RequestType,
            request.DeadlineSource,
            request.DeadlineAt,

            // Null for both, deliberately, and consistent with each other: the contract
            // refuses a demand that names a statute without somewhere to read it, and one
            // that cites anything at all on a courtesy deadline.
            StatuteCode: null,
            StatuteCitation: null);

    private static RemovalProgress Unsupported(string detail) =>
        new(
            RemovalJobStatus.Failed,
            RemovalRequestStatus.Failed,
            ConnectorFailureReason.Unsupported,

            // Nothing about the wiring changes by trying it again, which is what this
            // reason means.
            RetryWorthwhile: false,
            detail);

    /// <summary>
    /// Keeps a detail inside what the column holds.
    /// </summary>
    /// <remarks>
    /// A connector's detail is text this service did not write. Truncating it here means a
    /// long one costs a sentence rather than losing the whole attempt to a constraint —
    /// which would turn a company's bad day into a row that could not be saved.
    /// </remarks>
    private static string Trim(string detail) =>
        detail.Length <= 1000 ? detail : detail[..1000];
}
