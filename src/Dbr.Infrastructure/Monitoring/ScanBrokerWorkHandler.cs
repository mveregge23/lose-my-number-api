// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Messaging;
using Dbr.Domain.Monitoring;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dbr.Infrastructure.Monitoring;

/// <summary>
/// Asks one company what it holds, and writes down what came back.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not throw when a broker fails, and that is the important part.</b> Throwing
/// hands the message back to the transport, which redelivers it — with the same grant,
/// which is single-use and already spent. Every retry after the first would fail as a
/// refused release and record the wrong reason for the wrong thing. A leg that could not
/// be answered is therefore a finished leg with a reason on it, and trying again means
/// planning a fresh one with a fresh grant. Exceptions are left to escape only where they
/// mean what the handler contract says they mean: the work did not happen at all.
/// </para>
/// <para>
/// <b>The identity exists here for the length of one search and is never written down.</b>
/// It arrives decrypted from the process holding the keys, goes into the search, and is
/// gone. Nothing about it reaches a log line, the leg row, or the finding — a candidate
/// records which groups agreed and how closely, and the exposure records a number.
/// </para>
/// <para>
/// <b>The contract is checked on both sides of the search.</b> Before, because a context
/// carrying more of an identity than the search declared means the release handed over too
/// much and something has already been decrypted that should not have been. After, because
/// a finding claiming a match on a field the search never held is a claim it could not have
/// been in a position to make. Neither is a broker having a bad day, so both end the leg
/// without writing anything.
/// </para>
/// </remarks>
public sealed class ScanBrokerWorkHandler(
    DbrDbContext core,
    TenantContext tenantContext,
    IBrokerSearchRegistry searches,
    IReleaseClient releases,
    ScanCompletion completion,
    TimeProvider clock,
    ILogger<ScanBrokerWorkHandler> logger)
    : IBrokerWorkHandler<ScanBrokerWork>
{
    public async Task HandleAsync(ScanBrokerWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // The scope acts for this account from here on. Established from the message rather
        // than resolved from the grant, because reading the leg and writing a finding both
        // need a tenant before the grant is ever presented.
        tenantContext.SetTenant(work.TenantId);

        var leg = await core.Set<ScanLeg>()
            .FirstOrDefaultAsync(
                row => row.ScanId == work.ScanId && row.BrokerId == work.BrokerId,
                cancellationToken)
            .ConfigureAwait(false);

        if (leg is null)
        {
            // Nothing to record against. Left as a warning rather than an exception: this
            // message names a run this account does not have a leg for, and redelivering it
            // would produce the same nothing.
            logger.LogWarning(
                "Scan {ScanId} has no leg for broker {BrokerId}, so its work has nowhere to be "
                + "recorded. Discarding it.",
                work.ScanId,
                work.BrokerId);

            return;
        }

        if (leg.Outcome is not null)
        {
            // Already finished. A duplicate delivery, which the transport is entitled to
            // produce — and the grant it carries is spent, so going again would record a
            // refused release over a leg that had already answered.
            logger.LogInformation(
                "Scan {ScanId} leg for broker {BrokerId} has already finished as {Outcome}. "
                + "Ignoring a repeat delivery.",
                work.ScanId,
                work.BrokerId,
                leg.Outcome);

            return;
        }

        leg.StartedAt = clock.GetUtcNow();

        await RunAsync(work, leg, cancellationToken).ConfigureAwait(false);

        leg.CompletedAt = clock.GetUtcNow();

        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Scan {ScanId} leg for broker {BrokerId} finished as {Outcome}: {Found} candidates, "
            + "{Recorded} recorded.",
            work.ScanId,
            work.BrokerId,
            leg.Outcome,
            leg.CandidatesFound,
            leg.CandidatesRecorded);

        await completion.TrySettleAsync(work.ScanId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fills in the leg's outcome, and writes any findings worth showing.</summary>
    private async Task RunAsync(ScanBrokerWork work, ScanLeg leg, CancellationToken cancellationToken)
    {
        var search = searches.Find(work.BrokerId);

        if (search is null)
        {
            // The build changed between the plan and the run. Recorded rather than treated
            // as a fault, because a deploy that removed a search is a deliberate act.
            leg.Outcome = ScanLegOutcome.NoSearchAvailable;
            leg.Detail = "This build has no search for this company.";

            return;
        }

        // Read now rather than carried on the message, so that a domain corrected between
        // planning and running is the one that gets looked at. The lane is named by id for
        // the same reason.
        var domain = await core.Set<Broker>()
            .AsNoTracking()
            .Where(broker => broker.Id == work.BrokerId)
            .Select(broker => broker.Domain)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(domain))
        {
            leg.Outcome = ScanLegOutcome.Unsupported;
            leg.Detail = "This company is no longer in the catalog, so there is no site to look at.";

            return;
        }

        // Spent as late as possible: everything above can end the leg without an identity
        // being decrypted at all, and a grant burned before the leg turned out to have
        // nowhere to look would be a decryption nothing used.
        var released = await releases.RedeemAsync(work.ReleaseToken, cancellationToken)
            .ConfigureAwait(false);

        if (released is null)
        {
            leg.Outcome = ScanLegOutcome.ReleaseRefused;
            leg.Detail =
                "The grant for this leg would not open. It expired while the lane was busy, was "
                + "already spent, or names a run that has since stopped.";

            return;
        }

        var context = new SearchContext(
            work.ScanId,
            new SearchTarget(work.BrokerId, domain),
            Identity(released),
            work.AttemptNumber);

        if (SearchContract.Refuse(search.Capabilities, context) is { } refusal)
        {
            leg.Outcome = ScanLegOutcome.ContractBroken;
            leg.Detail = refusal;

            logger.LogError(
                "Scan {ScanId} leg for broker {BrokerId} was refused before it ran: {Refusal}",
                work.ScanId,
                work.BrokerId,
                refusal);

            return;
        }

        SearchResult result;

        try
        {
            result = await search.SearchAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping, which is not this leg's answer. Left to escape so
            // the transport keeps the message; the leg is still unfinished, and the grant
            // it carries has already been spent — so what re-runs it is a fresh plan.
            throw;
        }
        catch (Exception exception)
        {
            // A search that throws decided nothing. Recorded as its own outcome rather than
            // as a transient network problem, which would leave a bug looking like a
            // company having a bad day for as long as nobody read the log.
            leg.Outcome = ScanLegOutcome.Faulted;
            leg.Detail = $"The search threw {exception.GetType().Name}.";

            logger.LogError(
                exception,
                "The search for broker {BrokerId} threw while running scan {ScanId}.",
                work.BrokerId,
                work.ScanId);

            return;
        }

        if (SearchContract.Refuse(search.Capabilities, result) is { } broken)
        {
            leg.Outcome = ScanLegOutcome.ContractBroken;
            leg.Detail = broken;

            logger.LogError(
                "The search for broker {BrokerId} answered scan {ScanId} in a way it was not "
                + "entitled to: {Refusal}",
                work.BrokerId,
                work.ScanId,
                broken);

            return;
        }

        switch (result)
        {
            case SearchResult.Found found:
                Record(work, leg, found);
                break;

            case SearchResult.NothingFound:
                leg.Outcome = ScanLegOutcome.NothingFound;
                break;

            case SearchResult.Failed failed:
                leg.Outcome = Outcome(failed.Reason);
                leg.Detail = failed.Detail;
                break;
        }
    }

    /// <summary>
    /// Turns candidates into the findings somebody is shown, and counts the rest.
    /// </summary>
    /// <remarks>
    /// <b>What falls below the bar is not written anywhere.</b> An exposure is a durable
    /// record that a company probably holds this person's data, so rows nobody will ever be
    /// shown would retain more of somebody than this service does anything with. The count
    /// is what survives, and it is what tells an operator that a bar is set wrong — a
    /// company offering forty candidates a run and recording none is a search matching too
    /// loosely or a floor set too high, and neither is visible from the findings, because
    /// the ones that did not clear were never there.
    /// </remarks>
    private void Record(ScanBrokerWork work, ScanLeg leg, SearchResult.Found found)
    {
        var discoveredAt = clock.GetUtcNow();

        leg.Outcome = ScanLegOutcome.Found;
        leg.CandidatesFound = found.Candidates.Count;

        foreach (var candidate in found.Candidates)
        {
            var confidence = MatchConfidence.Score(candidate.Matches);

            if (!MatchConfidence.ClearsFloor(confidence))
            {
                continue;
            }

            core.Set<Exposure>().Add(new Exposure
            {
                Id = Guid.NewGuid(),
                TenantId = work.TenantId,
                ScanId = work.ScanId,
                PrivacyProfileId = work.PrivacyProfileId,
                BrokerId = work.BrokerId,
                Status = ExposureStatus.New,
                Confidence = confidence,
                DiscoveredAt = discoveredAt,

                // Nothing points at the listing yet. The pointer is a third party's copy of
                // somebody's identity and belongs in the vault, which is its own story —
                // until then two findings on one company are told apart by their confidence
                // and by nothing else.
            });

            leg.CandidatesRecorded++;
        }
    }

    /// <summary>
    /// The identity as the search takes it, rebuilt from what crossed the edge.
    /// </summary>
    /// <remarks>
    /// The profile's own type rather than a second one shaped like it, which is what the
    /// search context asks for and what refuses to print its own contents. A group the
    /// grant did not cover arrives empty, and empty is the honest answer either way — a
    /// group withheld and a group the profile has nothing in released the same nothing.
    /// </remarks>
    private static ProfileIdentityFields Identity(ReleaseResponse released) =>
        new(
            released.Names,
            [.. released.Addresses.Select(Address)],
            [.. released.Contacts.Select(Contact).OfType<ProfileContact>()],
            released.DateOfBirth);

    private static ProfileAddress Address(ReleasedAddress address) =>
        new(
            address.Id,
            address.Line1,
            address.Line2,
            address.City,
            address.Region,
            address.PostalCode,
            address.Country);

    /// <summary>
    /// One contact point, or nothing when its kind is not one this build knows.
    /// </summary>
    /// <remarks>
    /// Dropped rather than guessed at. A contact whose kind this build cannot name is one a
    /// search could not use anyway, and inventing a kind for it would hand a search a phone
    /// number it believed was an email address.
    /// </remarks>
    private static ProfileContact? Contact(ReleasedContact contact) =>
        Enum.TryParse<ProfileContactKind>(contact.Kind, ignoreCase: true, out var kind)
            ? new ProfileContact(contact.Id, kind, contact.Value)
            : null;

    /// <summary>How a search's account of a failure is recorded against the leg.</summary>
    private static ScanLegOutcome Outcome(SearchFailureReason reason) => reason switch
    {
        SearchFailureReason.Transient => ScanLegOutcome.Transient,
        SearchFailureReason.RateLimited => ScanLegOutcome.RateLimited,
        SearchFailureReason.PageShapeChanged => ScanLegOutcome.PageShapeChanged,
        SearchFailureReason.Blocked => ScanLegOutcome.Blocked,
        SearchFailureReason.Unsupported => ScanLegOutcome.Unsupported,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "Unmapped search failure. A way for a search to fail that a leg cannot record is "
            + "one that would leave the run unable to say why a company went unanswered."),
    };
}
