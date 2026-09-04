// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Consent;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dbr.Infrastructure.Removals;

/// <summary>
/// Opens demands and reads them back.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never decrypts anything.</b> Opening a demand needs the profile's id, whose it is
/// and which region it says the person lives in — all core-store columns. The identity
/// itself is not read here and does not need to be until a connector is about to fill in a
/// form, which is what the scoped release is for. Going through the profile service instead
/// would put the vault and the key manager on the path of writing a row.
/// </para>
/// <para>
/// <b>Consent is checked here rather than at the endpoint.</b> A listing that reappears
/// will reopen a demand through this same method with no HTTP request in front of it, and
/// a check living in a route handler is one that path would have to remember to repeat.
/// </para>
/// <para>
/// <b>The uniqueness rule is checked and also enforced underneath.</b> The query below
/// answers the ordinary case with a sentence somebody can read; the partial unique index is
/// what holds when two requests arrive at once, and the violation it raises is caught and
/// reported as the same outcome. Neither one alone is enough — a check without the index
/// loses the race, and an index without the check turns an ordinary duplicate into a 500.
/// </para>
/// </remarks>
public sealed class RemovalService(
    DbrDbContext core,
    IConsentService consent,
    IJurisdictionResolver jurisdictions,
    IOptions<RemovalOptions> options) : IRemovalService
{
    /// <summary>
    /// The Postgres SQLSTATE for a unique-constraint violation.
    /// </summary>
    /// <remarks>
    /// Matched on rather than parsing the message, which is localized and formatted for a
    /// person. The constraint name is checked alongside it so that this only ever absorbs
    /// the collision it is meant to: any other unique violation here is a bug, and turning
    /// it into "you already have one of these open" would hide it.
    /// </remarks>
    private const string UniqueViolation = "23505";

    /// <summary>The partial index holding one live demand per identity, company and kind.</summary>
    private const string OneOpenPerDemand = "removal_request_one_open_per_demand";

    private readonly RemovalOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<OpenRemovalResult> OpenAsync(
        Guid? profileId,
        Guid brokerId,
        LegalRequestType requestType,
        Guid? exposureId,
        CancellationToken cancellationToken)
    {
        if (!await consent.IsGrantedAsync(ConsentScope.AutoRemoval, cancellationToken)
            .ConfigureAwait(false))
        {
            // Before anything else, including before finding out whether the profile or the
            // broker exist. An account that has not permitted removals should not be able
            // to learn which ids are real by watching which errors come back.
            return OpenRemovalResult.Failed(OpenRemovalOutcome.ConsentMissing);
        }

        var profile = await FindProfileAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            return OpenRemovalResult.Failed(OpenRemovalOutcome.ProfileNotFound);
        }

        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == brokerId, cancellationToken)
            .ConfigureAwait(false);

        if (broker is null)
        {
            return OpenRemovalResult.Failed(OpenRemovalOutcome.UnknownBroker);
        }

        if (RemovalStrategies.ForMethod(broker.RemovalMethod) is not { } strategy)
        {
            return OpenRemovalResult.Failed(OpenRemovalOutcome.UnsupportedRemovalMethod);
        }

        var exposure = await ResolveExposureAsync(exposureId, cancellationToken).ConfigureAwait(false);

        if (exposure.Problem is { } problem)
        {
            return OpenRemovalResult.Failed(problem);
        }

        if (exposure.Listing is { } listing
            && (listing.PrivacyProfileId != profile.Id || listing.BrokerId != brokerId))
        {
            // The schema refuses each half of this with its own composite key. Getting
            // there first is what lets the answer say which of the two disagreed, rather
            // than surfacing a foreign-key name.
            return OpenRemovalResult.Failed(OpenRemovalOutcome.ExposureMismatch);
        }

        if (await IsAlreadyOpenAsync(profile.Id, brokerId, requestType, cancellationToken)
            .ConfigureAwait(false))
        {
            return OpenRemovalResult.Failed(OpenRemovalOutcome.AlreadyOpen);
        }

        var opened = DateTimeOffset.UtcNow;

        // Resolved once and written down. A statute corrected next year must not silently
        // reinterpret what somebody was told this year, which is why this is a snapshot on
        // the row rather than something the read path recomputes.
        var deadline = await jurisdictions
            .ResolveAsync(brokerId, profile.ResidencyRegion, requestType, opened, cancellationToken)
            .ConfigureAwait(false);

        var request = new RemovalRequest
        {
            Id = Guid.NewGuid(),
            TenantId = profile.TenantId,
            PrivacyProfileId = profile.Id,
            ExposureId = exposure.Listing?.Id,
            RequestType = requestType,
            BrokerId = brokerId,
            Status = RemovalRequestStatus.Queued,
            Strategy = strategy,

            // Nothing has been attempted. The dispatcher counts up from here as it writes
            // each job, so a demand that has been tried twice reads as two rather than as
            // a number somebody has to remember to have started at the right place.
            Attempt = 0,

            LegalBasisId = deadline.LegalBasisId,
            DeadlineSource = deadline.Source,
            DeadlineAt = deadline.DeadlineAt,
            CreatedAt = opened,
        };

        core.Set<RemovalRequest>().Add(request);

        if (exposure.Listing is { } cited)
        {
            // The dismiss route already refuses a listing with a demand open against it and
            // reads this column to decide. Leaving it unset would make that refusal
            // unreachable, so a person could tell us a listing is not them while a request
            // in their name over it was in flight.
            var tracked = await core.Set<Exposure>()
                .FirstAsync(row => row.Id == cited.Id, cancellationToken)
                .ConfigureAwait(false);

            tracked.Status = ExposureStatus.Requested;
        }

        try
        {
            await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (IsDuplicateDemand(error))
        {
            // Two requests arriving together. The index is what actually holds the rule;
            // this turns losing that race into the same answer the check above gives.
            return OpenRemovalResult.Failed(OpenRemovalOutcome.AlreadyOpen);
        }

        // Queued and left there. The lane it belongs in is declared and has no consumer,
        // so putting a message on one now would be work sitting where nothing reads it.
        return OpenRemovalResult.Opened(request, broker);
    }

    public async Task<IReadOnlyList<RemovalListing>> ListAsync(
        RemovalFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var requests = core.Set<RemovalRequest>().AsNoTracking();

        if (filter.Status is { } status)
        {
            requests = requests.Where(request => request.Status == status);
        }

        if (filter.ProfileId is { } profileId)
        {
            requests = requests.Where(request => request.PrivacyProfileId == profileId);
        }

        // Not paginated, for the reason the exposure list is not: a listing that comes back
        // reappears on the demand that removed it rather than opening a second one, so this
        // table is bounded by the catalog times the identities an account manages rather
        // than growing with time.
        //
        // Ordered before the join and not after it. Both read as though they should work
        // and only one does: the whole expression becomes a single statement whose ORDER BY
        // lands at the end either way, but sorting on a member of the projected pair is
        // something the translator cannot see through, and it fails at runtime rather than
        // at build. The ordering is pinned by a test over more than one row, because a
        // one-row list cannot tell a sorted answer from an unsorted one.
        var listings = await requests
            .OrderByDescending(request => request.CreatedAt)
            .Join(
                core.Set<Broker>().AsNoTracking(),
                request => request.BrokerId,
                broker => broker.Id,
                (request, broker) => new RemovalListing(request, broker))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return listings;
    }

    public async Task<RemovalListing?> FindAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await core.Set<RemovalRequest>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            // Somebody else's demand and one that was never opened answer alike, because
            // telling them apart would confirm that an id is in use elsewhere.
            return null;
        }

        var broker = await core.Set<Broker>()
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == request.BrokerId, cancellationToken)
            .ConfigureAwait(false);

        return new RemovalListing(request, broker);
    }

    public async Task<RemovalTimeline?> TimelineAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var listing = await FindAsync(requestId, cancellationToken).ConfigureAwait(false);

        if (listing is null)
        {
            return null;
        }

        var attempts = await core.Set<RemovalJob>()
            .AsNoTracking()
            .Where(job => job.RemovalRequestId == requestId)

            // Oldest first, unlike every list route here. This is a history read in order
            // rather than a feed of what happened lately, and the question it answers —
            // what has this demand been through — reads forwards.
            .OrderBy(job => job.AttemptNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RemovalTimeline(listing.Request, listing.Broker, attempts);
    }

    public Task<MoveRemovalResult> CancelAsync(Guid requestId, CancellationToken cancellationToken) =>
        MoveAsync(requestId, RemovalRequestStatus.Cancelled, cancellationToken);

    public Task<MoveRemovalResult> RetryAsync(Guid requestId, CancellationToken cancellationToken) =>
        MoveAsync(requestId, RemovalRequestStatus.Queued, cancellationToken);

    /// <summary>
    /// Moves one demand, if the lifecycle and its guards both allow it.
    /// </summary>
    /// <remarks>
    /// One method for both routes because the difference between them is the state asked
    /// for. What may follow what is the lifecycle's answer and is not restated here; what
    /// the lifecycle cannot answer is named on the transition as a guard, and evaluating
    /// those is exactly this method's job.
    /// </remarks>
    private async Task<MoveRemovalResult> MoveAsync(
        Guid requestId,
        RemovalRequestStatus to,
        CancellationToken cancellationToken)
    {
        var request = await core.Set<RemovalRequest>()
            .FirstOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return MoveRemovalResult.NotFound();
        }

        if (RemovalLifecycle.Find(request.Status, to) is not { } transition)
        {
            return MoveRemovalResult.NotAllowed(RemovalLifecycle.Refuse(request.Status, to)!);
        }

        if (transition.Guard == RemovalGuard.RetriesRemaining
            && request.Attempt >= _options.MaxAttempts)
        {
            return MoveRemovalResult.Refused(
                $"This request has been attempted {request.Attempt} times, which is as many as "
                + "this instance allows. Retrying again would send the same demand to a "
                + "company that has not answered the last three, which is the point at which "
                + "the answer is something other than trying harder.");
        }

        // The resubmit guard belongs to the reappearance path, which nothing drives yet.
        // It is not evaluated here because neither route can reach a transition carrying
        // it: cancelling is unguarded and retrying is guarded by attempts.
        var from = request.Status;

        request.Status = to;

        if (to == RemovalRequestStatus.Cancelled)
        {
            await ReleaseListingAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (IsDuplicateDemand(error))
        {
            // Retrying back into the queue while another live demand for the same identity,
            // company and kind exists. Only reachable if one was opened while this one sat
            // failed, since a failed request does not occupy the index.
            return MoveRemovalResult.Refused(
                "Another demand of this kind is already open with this company for this "
                + $"identity, so this one cannot go back in the queue. It stayed {Spell(from)}.");
        }

        return MoveRemovalResult.Moved(request);
    }

    /// <summary>
    /// Puts a cited listing back where it was before a demand was opened over it.
    /// </summary>
    /// <remarks>
    /// A cancelled demand is not an open one, and leaving the listing marked as having a
    /// request against it would block the person from dismissing it — with a refusal
    /// naming a request they had just called off. <see cref="ExposureStatus.New"/> is what
    /// "found, and nothing has been asked of the broker yet" means, which is true again.
    /// <para>
    /// A listing that had reappeared and was then demanded and cancelled comes back as new
    /// rather than as reappeared, which loses that it had been removed once. The history is
    /// still on the demand, which is the row that records having removed it; carrying the
    /// distinction here as well would need a column remembering what the status was before,
    /// and that is a second answer to a question the request already answers.
    /// </para>
    /// </remarks>
    private async Task ReleaseListingAsync(
        RemovalRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExposureId is not { } exposureId)
        {
            return;
        }

        var listing = await core.Set<Exposure>()
            .FirstOrDefaultAsync(row => row.Id == exposureId, cancellationToken)
            .ConfigureAwait(false);

        if (listing is { Status: ExposureStatus.Requested })
        {
            listing.Status = ExposureStatus.New;
        }
    }

    /// <summary>
    /// The identity a demand is for: the one named, or the tenant's own when none was.
    /// </summary>
    /// <remarks>
    /// Both paths read through the tenant query filter, so a profile belonging to another
    /// account is simply not found. The database says it again when the row is written —
    /// the request's foreign key is over the tenant and the profile together — which is
    /// what makes this a lookup rather than the check the guarantee rests on.
    /// </remarks>
    private async Task<PrivacyProfile?> FindProfileAsync(
        Guid? profileId,
        CancellationToken cancellationToken)
    {
        var profiles = core.Set<PrivacyProfile>().AsNoTracking();

        return profileId is { } id
            ? await profiles
                .FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken)
                .ConfigureAwait(false)
            : await profiles
                .FirstOrDefaultAsync(
                    profile => profile.RelationshipType == ProfileRelationship.Self,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>The cited listing, or why it cannot be cited.</summary>
    private async Task<(Exposure? Listing, OpenRemovalOutcome? Problem)> ResolveExposureAsync(
        Guid? exposureId,
        CancellationToken cancellationToken)
    {
        if (exposureId is not { } id)
        {
            return (null, null);
        }

        var listing = await core.Set<Exposure>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (listing is null)
        {
            return (null, OpenRemovalOutcome.ExposureNotFound);
        }

        return listing.Status == ExposureStatus.Dismissed
            ? (null, OpenRemovalOutcome.ExposureDismissed)
            : (listing, null);
    }

    /// <summary>Whether this identity already has a live demand of this kind here.</summary>
    /// <remarks>
    /// The states excluded are the index's, not a second opinion about them. Expired and
    /// cancelled are dead and do not block a fresh demand; removed deliberately still does,
    /// because a listing that comes back belongs to the request that removed it.
    /// </remarks>
    private async Task<bool> IsAlreadyOpenAsync(
        Guid profileId,
        Guid brokerId,
        LegalRequestType requestType,
        CancellationToken cancellationToken) =>
        await core.Set<RemovalRequest>()
            .AsNoTracking()
            .AnyAsync(
                request => request.PrivacyProfileId == profileId
                    && request.BrokerId == brokerId
                    && request.RequestType == requestType
                    && request.Status != RemovalRequestStatus.Expired
                    && request.Status != RemovalRequestStatus.Cancelled,
                cancellationToken)
            .ConfigureAwait(false);

    private static bool IsDuplicateDemand(DbUpdateException error) =>
        error.InnerException is PostgresException
        {
            SqlState: UniqueViolation,
            ConstraintName: OneOpenPerDemand,
        };

    private static string Spell(RemovalRequestStatus status) =>
        RemovalVocabulary.ToWire(status).Replace('_', ' ');
}
