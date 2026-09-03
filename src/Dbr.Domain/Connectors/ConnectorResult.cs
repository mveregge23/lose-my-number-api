// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Connectors;

/// <summary>What a person is being asked to do.</summary>
/// <remarks>
/// The kind is separate from the instructions because it decides <i>who</i> may be asked,
/// not just what the asking says. A CAPTCHA is a puzzle about the company's page and could
/// in principle go to anybody; an identity document is the person's passport and can only
/// ever go to them. Collapsing these into free text would leave that distinction to
/// whoever reads the sentence.
/// </remarks>
public enum HumanInputKind
{
    /// <summary>A challenge on the company's page that a script cannot clear.</summary>
    Captcha,

    /// <summary>A link the company mailed, which has to be followed to confirm the demand.</summary>
    EmailConfirmation,

    /// <summary>Proof of identity the company requires before it will act.</summary>
    IdentityDocument,

    /// <summary>Something else, described in the instructions.</summary>
    Custom,
}

/// <summary>
/// The stop a connector reached, in terms somebody can act on.
/// </summary>
/// <remarks>
/// <b><see cref="ChallengeRef"/> is about the company's page and never about the person.</b>
/// A reference to a CAPTCHA image can be rendered to whoever resolves the ask without any
/// further release from the vault, which is the whole reason it is allowed to exist here —
/// a field that could carry part of an identity would need one, and would have turned
/// showing somebody a puzzle into a decryption.
/// </remarks>
/// <param name="Instructions">
/// What to do, written for the person who has to do it. Never empty: an ask nobody can act
/// on parks the demand indefinitely while looking like progress.
/// </param>
public sealed record HumanInputRequest(
    HumanInputKind Kind,
    string Instructions,
    Uri? ChallengeRef);

/// <summary>
/// Why a connector could not complete an attempt.
/// </summary>
/// <remarks>
/// The search side's taxonomy, plus the one that only has meaning here. A company can
/// decline a demand — it can say the person is not who they claim, or that the right does
/// not reach it — and that is an answer rather than a fault. A search asks nothing of
/// anybody and so has nothing that can be declined.
/// </remarks>
public enum ConnectorFailureReason
{
    /// <summary>A timeout, a connection reset, a 5xx. Nothing about this demand in particular.</summary>
    Transient,

    /// <summary>The company throttled this instance and said so.</summary>
    RateLimited,

    /// <summary>
    /// The page was reachable and no longer looks like what the connector expects.
    /// </summary>
    /// <remarks>
    /// Distinct from a transient failure because retrying cannot help and because it is the
    /// one failure that is a message to whoever maintains the catalog rather than to the
    /// dispatcher. A connector reporting this as transient would burn every attempt and
    /// leave the entry looking flaky rather than stale.
    /// </remarks>
    BrokerFormChanged,

    /// <summary>
    /// The company received the demand and refused it.
    /// </summary>
    /// <remarks>
    /// The one failure here that is the company exercising a judgement rather than
    /// something going wrong, and the one that must never be retried: sending the same
    /// demand again after a refusal is not persistence, it is ignoring an answer somebody
    /// is entitled to give. What it is worth is a record — a company refusing at a rate
    /// nobody else does is a fact about that company.
    /// </remarks>
    Rejected,

    /// <summary>The connector cannot do what this attempt asks of it.</summary>
    /// <remarks>
    /// A configuration fault rather than a runtime one — an identity missing a field the
    /// connector cannot work without, a site variant it does not handle. It says the wiring
    /// is wrong, so retrying the same wiring is pointless.
    /// </remarks>
    Unsupported,
}

/// <summary>
/// What one attempt at one removal came back with.
/// </summary>
/// <remarks>
/// <para>
/// A closed hierarchy: the five cases below are the only ones there are, because the base
/// constructor is private and only its own nested types can reach it. A connector compiled
/// against this cannot invent a sixth outcome the dispatcher has no branch for, and the
/// dispatcher's switch over these is exhaustive by construction rather than by a default
/// case that quietly means "something else happened".
/// </para>
/// <para>
/// <b>None of these says the listing is gone.</b> A connector submits and reports; whether
/// a company honoured the demand is separated from the submission by however long the
/// deadline runs, and only a verification scan closes that gap. An outcome here is about
/// the attempt.
/// </para>
/// </remarks>
public abstract record ConnectorResult
{
    private ConnectorResult()
    {
    }

    /// <summary>
    /// The connector did something: submitted a form, sent a message, called an API.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as <see cref="AlreadyClear"/>, and the difference is the audit
    /// trail's.</b> Both mean nobody has to try again, and conflating them would leave the
    /// record unable to say whether a demand was ever actually made — which is the one
    /// question somebody asks when a company later claims it never received one.
    /// </remarks>
    /// <param name="ReceiptRef">
    /// A confirmation the company issued — a ticket number, a reference on a receipt page —
    /// or <see langword="null"/> when it issued none. It is the evidence a demand was made,
    /// so it is worth keeping even though most companies give nothing.
    /// </param>
    /// <param name="VerifyNotBefore">
    /// The earliest a verification scan is worth running, or <see langword="null"/> to let
    /// the deadline decide. The connector's call because it is the thing that read the
    /// page: a company that says it processes requests in ten days has told us something
    /// the catalog's own target does not know.
    /// </param>
    public sealed record Success(string? ReceiptRef, DateTimeOffset? VerifyNotBefore) : ConnectorResult;

    /// <summary>
    /// The connector looked, and there was nothing to remove.
    /// </summary>
    /// <remarks>
    /// The listing was stale, or somebody had already opted out by hand. Nothing was asked
    /// of the company, so there is no receipt and nothing to wait for — which is exactly
    /// what makes it worth telling apart from <see cref="Success"/>.
    /// </remarks>
    public sealed record AlreadyClear : ConnectorResult;

    /// <summary>
    /// The connector reached a step no script completes, and stopped.
    /// </summary>
    /// <remarks>
    /// <b>A hard stop rather than a pause.</b> Nothing keeps a live session parked while a
    /// person is found and asked — that is expensive, and a long-lived session sitting in a
    /// worker process holding somebody's identity is the standing state this design spends
    /// most of its effort avoiding. So the connector writes down what it needs to pick back
    /// up and the attempt ends there.
    /// </remarks>
    /// <param name="Checkpoint">
    /// Whatever the connector needs to resume, opaque to everything else and never a
    /// password. Never empty: a stop that saved nothing cannot be resumed, which makes it a
    /// failure wearing the shape of a pause.
    /// </param>
    public sealed record RequiresHumanInput(HumanInputRequest Ask, byte[] Checkpoint) : ConnectorResult;

    /// <summary>
    /// The demand is in, and the clock is running.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Success"/> because the two ask different things of the
    /// dispatcher: one is an attempt that finished, the other is an attempt that finished
    /// and left something to come back to on a date. It is also how a company granting
    /// itself an extension is absorbed without new machinery — a connector that reads an
    /// extension notice returns this again with the revised date.
    /// </remarks>
    /// <param name="Deadline">When an answer is due, as the company itself stated it.</param>
    public sealed record AwaitingBrokerResponse(DateTimeOffset Deadline, byte[]? Checkpoint) : ConnectorResult;

    /// <summary>
    /// The attempt did not complete.
    /// </summary>
    /// <param name="Detail">
    /// What actually happened, for whoever reads the log. Never the identity the demand was
    /// made for, and never the page's content — a status line, a selector that did not
    /// match, the name of the timeout that expired.
    /// </param>
    /// <param name="Retryable">
    /// The connector's own call, not the dispatcher's. The reason narrows it — nothing
    /// retries a refusal or a changed page — but within a reason the connector is the only
    /// thing that saw what happened: a connection reset and a host that no longer resolves
    /// are both transient by category, and only one of them is worth another attempt.
    /// </param>
    public sealed record Failed(
        ConnectorFailureReason Reason,
        string Detail,
        bool Retryable) : ConnectorResult;
}
