// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Removals;

/// <summary>
/// A condition a transition depends on that this table cannot evaluate.
/// </summary>
/// <remarks>
/// Named here rather than checked here, deliberately. Whether attempts remain is a fact
/// about a row, and whether resubmission was consented to is a question for the consent
/// service — neither belongs in a static table of what follows what. Naming them keeps
/// the obligation visible to whoever performs the transition instead of leaving it as
/// something to remember.
/// </remarks>
public enum RemovalGuard
{
    /// <summary>Nothing beyond the states themselves.</summary>
    None,

    /// <summary>Only while the request has attempts left.</summary>
    RetriesRemaining,

    /// <summary>
    /// Only if the tenant currently permits <c>auto_resubmit</c>.
    /// </summary>
    /// <remarks>
    /// The one transition in this lifecycle that puts a message out in somebody's name
    /// without them asking at that moment. Checked against the decision in force when the
    /// listing reappears, not the one in force when the request was opened.
    /// </remarks>
    ResubmitConsent,
}

/// <param name="Reason">What causes this transition, in the terms §5 uses.</param>
public sealed record RemovalTransition(
    RemovalRequestStatus From,
    RemovalRequestStatus To,
    string Reason,
    RemovalGuard Guard);

/// <summary>
/// What may follow what, for a removal request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enforced in code rather than by the database, unlike most rules here.</b> A table of
/// legal transitions is not something a check constraint can express — it is a statement
/// about the previous value of a column, which only a trigger can see. A trigger would put
/// this table in plpgsql, where it cannot be unit-tested, cannot be read beside the code
/// that depends on it, and has to be kept in step with the enum by hand. The trade is
/// deliberate and it is the weaker of the two positions the rest of this schema takes: a
/// write that bypasses this type will not be refused. What the database does still hold is
/// that the value is one of the nine, and that a request belongs to the account its
/// exposure does.
/// </para>
/// <para>
/// <b>Terminal means terminal.</b> Only <see cref="RemovalRequestStatus.Expired"/> and
/// <see cref="RemovalRequestStatus.Cancelled"/> have nothing after them.
/// <see cref="RemovalRequestStatus.Removed"/> looks terminal and is not, which is the
/// whole reason monitoring recurs: a listing that came back is the same listing, and it
/// returns to the request that already knows how many times this has happened.
/// </para>
/// </remarks>
public static class RemovalLifecycle
{
    /// <summary>Every transition §5 allows, and the one §6.5 requires.</summary>
    public static IReadOnlyList<RemovalTransition> All { get; } =
    [
        new(RemovalRequestStatus.Queued, RemovalRequestStatus.Submitted,
            "a worker dispatched it", RemovalGuard.None),

        new(RemovalRequestStatus.Queued, RemovalRequestStatus.Cancelled,
            "the tenant called it off before it was sent", RemovalGuard.None),

        new(RemovalRequestStatus.Submitted, RemovalRequestStatus.AwaitingBrokerResponse,
            "the broker accepted it and the clock started", RemovalGuard.None),

        new(RemovalRequestStatus.Submitted, RemovalRequestStatus.RequiresHumanInput,
            "the connector reached a step no script completes", RemovalGuard.None),

        // §5's diagram has no edge here and §9.2 requires one: it says a connector that
        // looked and found nothing to remove maps to Removed, the same as one that acted.
        // Without this the two answers a connector can give about a finished demand have
        // only one state between them, and the honest one is unreachable. Waiting for a
        // verification scan instead would be waiting on a company that was never asked
        // anything, which is a deadline running against nobody.
        new(RemovalRequestStatus.Submitted, RemovalRequestStatus.Removed,
            "the connector found nothing left to remove", RemovalGuard.None),

        new(RemovalRequestStatus.Submitted, RemovalRequestStatus.Failed,
            "the submission itself failed", RemovalGuard.None),

        new(RemovalRequestStatus.Submitted, RemovalRequestStatus.Cancelled,
            "the tenant called it off before it was answered", RemovalGuard.None),

        new(RemovalRequestStatus.RequiresHumanInput, RemovalRequestStatus.AwaitingBrokerResponse,
            "the tenant supplied what was asked and the connector resumed", RemovalGuard.None),

        new(RemovalRequestStatus.AwaitingBrokerResponse, RemovalRequestStatus.Removed,
            "a verification scan confirmed the listing is gone", RemovalGuard.None),

        new(RemovalRequestStatus.AwaitingBrokerResponse, RemovalRequestStatus.Failed,
            "the deadline passed with no response", RemovalGuard.None),

        new(RemovalRequestStatus.Failed, RemovalRequestStatus.Queued,
            "retried", RemovalGuard.RetriesRemaining),

        new(RemovalRequestStatus.Failed, RemovalRequestStatus.Expired,
            "retries exhausted", RemovalGuard.None),

        new(RemovalRequestStatus.Removed, RemovalRequestStatus.Reappeared,
            "a later verification scan found the listing again", RemovalGuard.None),

        new(RemovalRequestStatus.Reappeared, RemovalRequestStatus.Queued,
            "resubmitted automatically", RemovalGuard.ResubmitConsent),
    ];

    /// <summary>The transition between these two states, or <see langword="null"/>.</summary>
    public static RemovalTransition? Find(RemovalRequestStatus from, RemovalRequestStatus to) =>
        All.FirstOrDefault(transition => transition.From == from && transition.To == to);

    /// <summary>Whether a request may move from one state to the other at all.</summary>
    /// <remarks>
    /// Says nothing about whether it may <i>right now</i> — a transition carrying a
    /// <see cref="RemovalGuard"/> is allowed by this table and still conditional on
    /// something only the caller can see.
    /// </remarks>
    public static bool IsAllowed(RemovalRequestStatus from, RemovalRequestStatus to) =>
        Find(from, to) is not null;

    /// <summary>Everywhere a request in this state can go next.</summary>
    public static IReadOnlyList<RemovalRequestStatus> NextFrom(RemovalRequestStatus status) =>
        [.. All.Where(transition => transition.From == status).Select(transition => transition.To)];

    /// <summary>Whether nothing follows this state.</summary>
    public static bool IsTerminal(RemovalRequestStatus status) => NextFrom(status).Count == 0;

    /// <summary>
    /// Why a move is refused, or <see langword="null"/> when the table allows it.
    /// </summary>
    /// <remarks>
    /// A sentence rather than a boolean, because this is the text that ends up in front of
    /// somebody. "A removed request cannot be cancelled" is actionable; "invalid state
    /// transition" sends them to read the source.
    /// </remarks>
    public static string? Refuse(RemovalRequestStatus from, RemovalRequestStatus to)
    {
        if (IsAllowed(from, to))
        {
            return null;
        }

        if (IsTerminal(from))
        {
            return $"This request is {from.ToString().ToLowerInvariant()}, which is where it ends. "
                + "Nothing moves it from there.";
        }

        var next = string.Join(", ", NextFrom(from).Select(status => status.ToString().ToLowerInvariant()));

        return $"A request that is {from.ToString().ToLowerInvariant()} cannot become "
            + $"{to.ToString().ToLowerInvariant()}. From here it can only become: {next}.";
    }
}
