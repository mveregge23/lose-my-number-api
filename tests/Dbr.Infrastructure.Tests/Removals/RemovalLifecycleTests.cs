// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Removals;

namespace Dbr.Infrastructure.Tests.Removals;

/// <summary>
/// What may follow what, for a removal request.
/// </summary>
/// <remarks>
/// Asserted exhaustively rather than by example. This table is the only thing standing
/// between the lifecycle and a status column that can be written to anything — the
/// database holds the value to one of nine but says nothing about which nine follow which
/// — so the interesting failures are the pairs nobody thought to write a test for.
/// </remarks>
public class RemovalLifecycleTests
{
    /// <summary>
    /// §5's diagram, plus the cancel route §6.5 requires, written out independently.
    /// </summary>
    /// <remarks>
    /// Deliberately a second copy rather than something derived from
    /// <see cref="RemovalLifecycle.All"/>. A test that read the table it is checking would
    /// pass for any table at all; this one fails if the two disagree, which is the point.
    /// </remarks>
    private static readonly (RemovalRequestStatus From, RemovalRequestStatus To)[] Expected =
    [
        (RemovalRequestStatus.Queued, RemovalRequestStatus.Submitted),
        (RemovalRequestStatus.Queued, RemovalRequestStatus.Cancelled),
        (RemovalRequestStatus.Submitted, RemovalRequestStatus.AwaitingBrokerResponse),
        (RemovalRequestStatus.Submitted, RemovalRequestStatus.RequiresHumanInput),
        (RemovalRequestStatus.Submitted, RemovalRequestStatus.Failed),
        (RemovalRequestStatus.Submitted, RemovalRequestStatus.Cancelled),
        (RemovalRequestStatus.RequiresHumanInput, RemovalRequestStatus.AwaitingBrokerResponse),
        (RemovalRequestStatus.AwaitingBrokerResponse, RemovalRequestStatus.Removed),
        (RemovalRequestStatus.AwaitingBrokerResponse, RemovalRequestStatus.Failed),
        (RemovalRequestStatus.Failed, RemovalRequestStatus.Queued),
        (RemovalRequestStatus.Failed, RemovalRequestStatus.Expired),
        (RemovalRequestStatus.Removed, RemovalRequestStatus.Reappeared),
        (RemovalRequestStatus.Reappeared, RemovalRequestStatus.Queued),
    ];

    public static TheoryData<RemovalRequestStatus, RemovalRequestStatus> EveryPair
    {
        get
        {
            var data = new TheoryData<RemovalRequestStatus, RemovalRequestStatus>();

            foreach (var from in Enum.GetValues<RemovalRequestStatus>())
            {
                foreach (var to in Enum.GetValues<RemovalRequestStatus>())
                {
                    data.Add(from, to);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Exactly_the_transitions_the_design_allows_are_allowed(
        RemovalRequestStatus from,
        RemovalRequestStatus to)
    {
        // Eighty-one pairs, thirteen of them legal. The ones worth catching are the
        // plausible-sounding illegal ones — removed straight back to queued, cancelled
        // reopened, submitted marked removed without anything having verified it.
        var expected = Expected.Contains((from, to));

        Assert.Equal(expected, RemovalLifecycle.IsAllowed(from, to));
    }

    [Fact]
    public void No_pair_appears_twice()
    {
        // Two rows for one pair would make Find return whichever came first, and the two
        // could carry different guards — so a transition needing consent could silently
        // resolve to one that does not.
        var pairs = RemovalLifecycle.All.Select(t => (t.From, t.To)).ToList();

        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void Nothing_follows_expired_or_cancelled()
    {
        Assert.True(RemovalLifecycle.IsTerminal(RemovalRequestStatus.Expired));
        Assert.True(RemovalLifecycle.IsTerminal(RemovalRequestStatus.Cancelled));
    }

    [Fact]
    public void Removed_is_not_terminal()
    {
        // The claim the whole design rests on. A listing confirmed gone stays under watch,
        // because brokers re-buy and re-scrape — and if this ever becomes terminal, the
        // service quietly stops noticing that somebody's data came back.
        Assert.False(RemovalLifecycle.IsTerminal(RemovalRequestStatus.Removed));
        Assert.Equal([RemovalRequestStatus.Reappeared], RemovalLifecycle.NextFrom(RemovalRequestStatus.Removed));
    }

    [Fact]
    public void Every_state_is_reachable_from_queued()
    {
        // A state nothing leads to is a state the code can never be in, which means either
        // a missing transition or an enum member that should not exist. Either way it is
        // worth knowing before something tries to write it.
        var seen = new HashSet<RemovalRequestStatus> { RemovalRequestStatus.Queued };
        var frontier = new Queue<RemovalRequestStatus>([RemovalRequestStatus.Queued]);

        while (frontier.Count > 0)
        {
            foreach (var next in RemovalLifecycle.NextFrom(frontier.Dequeue()))
            {
                if (seen.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        Assert.Equal(Enum.GetValues<RemovalRequestStatus>().ToHashSet(), seen);
    }

    [Fact]
    public void Only_two_transitions_carry_a_guard_and_they_are_the_right_two()
    {
        // Both are transitions that cause something to go out in somebody's name. Losing
        // either guard would not break anything visibly: it would just mean retrying past
        // the limit, or resubmitting for somebody who withdrew permission.
        Assert.Equal(
            RemovalGuard.RetriesRemaining,
            RemovalLifecycle.Find(RemovalRequestStatus.Failed, RemovalRequestStatus.Queued)!.Guard);

        Assert.Equal(
            RemovalGuard.ResubmitConsent,
            RemovalLifecycle.Find(RemovalRequestStatus.Reappeared, RemovalRequestStatus.Queued)!.Guard);

        Assert.Equal(
            2,
            RemovalLifecycle.All.Count(transition => transition.Guard != RemovalGuard.None));
    }

    [Fact]
    public void Every_transition_says_what_causes_it()
    {
        Assert.All(
            RemovalLifecycle.All,
            transition => Assert.False(string.IsNullOrWhiteSpace(transition.Reason)));
    }

    [Fact]
    public void A_permitted_move_is_not_refused()
    {
        Assert.All(
            RemovalLifecycle.All,
            transition => Assert.Null(RemovalLifecycle.Refuse(transition.From, transition.To)));
    }

    [Fact]
    public void A_refusal_from_a_terminal_state_says_it_is_the_end()
    {
        var problem = RemovalLifecycle.Refuse(RemovalRequestStatus.Cancelled, RemovalRequestStatus.Queued);

        Assert.NotNull(problem);
        Assert.Contains("where it ends", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_elsewhere_names_what_is_possible_instead()
    {
        // The text somebody actually reads. "Invalid state transition" sends them to the
        // source; naming the alternatives answers the question they were about to ask.
        var problem = RemovalLifecycle.Refuse(RemovalRequestStatus.Removed, RemovalRequestStatus.Queued);

        Assert.NotNull(problem);
        Assert.Contains("reappeared", problem, StringComparison.Ordinal);
    }
}
