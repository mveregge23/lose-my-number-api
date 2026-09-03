// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Profiles;

namespace Dbr.Domain.Connectors;

/// <summary>
/// The company being asked, in the only terms a connector needs it.
/// </summary>
/// <remarks>
/// A few fields rather than the catalog row, for the reason the search side gives: most of
/// what the catalog holds about a company is about how this instance treats it — how fast
/// its lane runs, when the breaker opens, how long a removal is given — and a connector
/// that could read its own pacing would be a connector that could argue with it.
/// <para>
/// There is no address or opt-out URL here, and that is not an omission. A recipe writes a
/// path or a mailbox's local part and the origin is built from <see cref="Domain"/> in
/// code, so a reviewed document cannot come to name a different company than the one the
/// job is for — the same rule the search recipes are held to, and the same reason: a
/// document that could name an address could send somebody's name anywhere, and would
/// arrive for review looking like a changed string.
/// </para>
/// </remarks>
/// <param name="BrokerId">
/// The catalog's identity for the company. Carried so an attempt is filed against the right
/// row even after a domain is corrected, which is the same reason lanes are named by id.
/// </param>
/// <param name="Domain">The site to act against, and the origin every reference is built from.</param>
/// <param name="Method">
/// How this company accepts a demand. Here so a connector resolved for the wrong kind of
/// company is refused before it runs rather than after it fails.
/// </param>
public sealed record ConnectorTarget(Guid BrokerId, string Domain, RemovalMethod Method);

/// <summary>
/// What is being demanded, and on whose authority.
/// </summary>
/// <remarks>
/// <para>
/// A connector composing a message or filling a form is making a claim in somebody else's
/// name, and the claim has two halves that must not come apart: which right is being
/// exercised, and whether anything obliges the company to honour it. A deletion demand and
/// an opt-out of sale are different sentences with different deadlines, and a message that
/// cited a statute when none was found to govern would be an assertion this service made up.
/// </para>
/// <para>
/// <b>The citation travels with the deadline rather than being looked up.</b> The regime
/// that governed was resolved once, when the request was created, and written down with the
/// date it produced. Handing a connector the identifier and letting it fetch the statute
/// would let a correction made next year silently rewrite what somebody was told this year,
/// and would give a connector a reason to read the catalog.
/// </para>
/// </remarks>
/// <param name="Source">
/// Whether a statute set the deadline or the company's own target did. The two fields below
/// are present exactly when this is <see cref="DeadlineSource.Statutory"/>.
/// </param>
/// <param name="StatuteCode">
/// The short name of the regime, as a message would cite it, or <see langword="null"/> when
/// no statute was found to reach this company for this person.
/// </param>
/// <param name="StatuteCitation">
/// Where the text of that regime can be read, or <see langword="null"/> for the same reason.
/// A company receiving a demand should be able to check it without taking our word for it.
/// </param>
public sealed record ConnectorDemand(
    LegalRequestType RequestType,
    DeadlineSource Source,
    DateTimeOffset DeadlineAt,
    string? StatuteCode,
    Uri? StatuteCitation);

/// <summary>
/// Everything one attempt at one removal gets.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ReleasedIdentity"/> is a copy, not a handle.</b> It holds the values the
/// vault released for this attempt and there is no way from here to ask for more — a
/// connector that turns out to need a date of birth fails rather than fetches one, and the
/// fix is a review of what it declares rather than a decision it makes at runtime. The
/// groups it does not carry are simply absent; a group present but empty means the profile
/// has none on file, which is an answer rather than a refusal.
/// </para>
/// <para>
/// The identity type is the profile's own rather than a second one shaped like it, and the
/// choice carries the property that matters most here: it refuses to print its own
/// contents, so a connector that logs its context logs how many names it was given rather
/// than what they are.
/// </para>
/// <para>
/// There is no tenant on this row and no profile id, deliberately. A connector has no
/// legitimate use for either, and a field that exists is a field that ends up in a log line,
/// a queue message, or a company's inbox by way of a template somebody built out of
/// "everything we know".
/// </para>
/// <para>
/// <b>There is no reply address either, and there does not need to be one.</b> The address a
/// company's answer comes back to is derived from <see cref="JobId"/>, so it is the same
/// address whoever receives the reply resolves the job by. A field carrying it would be a
/// second spelling of one fact, and the two would disagree on the day one of them was built
/// wrong.
/// </para>
/// </remarks>
/// <param name="JobId">
/// This attempt. It is the idempotency key a connector should present to a company that
/// offers one, so that a redelivered message does not become a second demand.
/// </param>
/// <param name="RemovalRequestId">
/// The demand this is an attempt at. Attempts are correlated by it — a connector invoked
/// twice for one request is the same demand made twice, not two demands.
/// </param>
/// <param name="SourceRef">
/// The listing that prompted the demand, or <see langword="null"/> when none did. Null is
/// ordinary rather than exceptional: nothing about the right to tell a company to delete
/// what it holds depends on having found a page with your name on it first, and an opt-out
/// of sale is prospective. A connector that can cite a specific listing should; one handed
/// nothing asks about the person rather than about a page.
/// </param>
/// <param name="Checkpoint">
/// Opaque resume state from a previous attempt that stopped, or <see langword="null"/> when
/// there is none. Meaningful only to the connector that wrote it.
/// </param>
/// <param name="AttemptNumber">
/// Which try this is, from one. A connector that wants to behave differently on a retry
/// can; most will not, and the number is here so the ones that do are not left inferring it
/// from state they would have to keep between attempts.
/// </param>
public sealed record ConnectorContext(
    Guid JobId,
    Guid RemovalRequestId,
    ConnectorTarget Broker,
    ConnectorDemand Demand,
    ProfileIdentityFields ReleasedIdentity,
    Uri? SourceRef,
    byte[]? Checkpoint,
    int AttemptNumber)
{
    /// <summary>
    /// Names the type and withholds the two members that are somebody's identity.
    /// </summary>
    /// <remarks>
    /// The released identity refuses to print itself, so it would be safe on its own. The
    /// other two are not. A company's profile URL routinely spells out the name, the city
    /// and sometimes the age of the person it is about, which makes it a copy of the
    /// identity rather than a pointer to one; and a checkpoint is a partly-filled form,
    /// which is the same data in a shape nobody would think to look at. A record prints
    /// every member it has, so both are one interpolation away from a log line.
    /// <para>
    /// What is left is what somebody following a failure through a log actually needs — the
    /// attempt, the demand, the company — and none of it is about a person.
    /// </para>
    /// </remarks>
    public override string ToString() =>
        $"ConnectorContext {{ JobId = {JobId}, RemovalRequestId = {RemovalRequestId}, "
        + $"Broker = {Broker}, Demand = {Demand}, AttemptNumber = {AttemptNumber}, "
        + $"Identity = {ReleasedIdentity}, [withheld] }}";
}
