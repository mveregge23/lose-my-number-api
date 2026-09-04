// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Dbr.Domain.Connectors;
using Dbr.Domain.Removals;

namespace Dbr.Infrastructure.Tests.Removals;

/// <summary>
/// What each of a connector's five answers does to the attempt and to the demand.
/// </summary>
/// <remarks>
/// The substance of dispatching, and the part that can be checked without a database. Two
/// claims run through all of it: that the attempt and the demand are answering different
/// questions, and that every branch lands somewhere the lifecycle actually allows from
/// where a dispatched demand sits.
/// </remarks>
public class RemovalOutcomeTests
{
    private static readonly DateTimeOffset Deadline =
        new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A demand a company accepted is waiting, not removed.
    /// </summary>
    /// <remarks>
    /// The distinction the whole verification half of this system rests on: a company
    /// saying it has the request is not a company having acted on it, and reporting the
    /// first as the second would tell somebody their data is gone on the strength of a
    /// confirmation page.
    /// </remarks>
    [Fact]
    public void An_accepted_demand_starts_the_clock_rather_than_ending_it()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.Success("TICKET-1", null));

        Assert.Equal(RemovalJobStatus.Succeeded, progress.JobStatus);
        Assert.Equal(RemovalRequestStatus.AwaitingBrokerResponse, progress.RequestStatus);
        Assert.Null(progress.FailureReason);
        Assert.False(progress.RetryWorthwhile);
    }

    [Fact]
    public void A_receipt_is_kept_where_one_was_issued()
    {
        var withReceipt = RemovalOutcomes.For(new ConnectorResult.Success("TICKET-4471", null));
        var without = RemovalOutcomes.For(new ConnectorResult.Success(null, null));

        Assert.Contains("TICKET-4471", withReceipt.Detail, StringComparison.Ordinal);
        Assert.Contains("no receipt", without.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to remove ends the demand, and is the one branch that reaches removed early.
    /// </summary>
    /// <remarks>
    /// §9.2 says a connector that looked and found nothing maps to removed, the same as one
    /// that acted. §5's diagram draws no edge for it. Waiting instead would be a deadline
    /// running against a company that was never asked anything.
    /// </remarks>
    [Fact]
    public void Nothing_left_to_remove_ends_the_demand()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.AlreadyClear());

        Assert.Equal(RemovalJobStatus.Succeeded, progress.JobStatus);
        Assert.Equal(RemovalRequestStatus.Removed, progress.RequestStatus);
        Assert.False(progress.RetryWorthwhile);
    }

    /// <summary>
    /// A stop is a successful attempt and a parked demand.
    /// </summary>
    /// <remarks>
    /// The clearest case of the two statuses answering different questions. The attempt did
    /// everything a script can and said what it needs; recording it as failed would put a
    /// working connector in the same column as a broken one.
    /// </remarks>
    [Fact]
    public void A_stop_for_a_person_is_not_a_failed_attempt()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.RequiresHumanInput(
            new HumanInputRequest(HumanInputKind.Captcha, "Solve the puzzle shown.", null),
            Encoding.UTF8.GetBytes("draft")));

        Assert.Equal(RemovalJobStatus.Succeeded, progress.JobStatus);
        Assert.Equal(RemovalRequestStatus.RequiresHumanInput, progress.RequestStatus);
        Assert.False(progress.RetryWorthwhile);
    }

    [Fact]
    public void A_demand_the_company_will_answer_later_is_waiting()
    {
        var progress = RemovalOutcomes.For(
            new ConnectorResult.AwaitingBrokerResponse(Deadline, null));

        Assert.Equal(RemovalJobStatus.Succeeded, progress.JobStatus);
        Assert.Equal(RemovalRequestStatus.AwaitingBrokerResponse, progress.RequestStatus);
    }

    [Fact]
    public void A_failure_carries_the_reason_forward()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.Failed(
            ConnectorFailureReason.Transient,
            "the connection was reset",
            Retryable: true));

        Assert.Equal(RemovalJobStatus.Failed, progress.JobStatus);
        Assert.Equal(RemovalRequestStatus.Failed, progress.RequestStatus);
        Assert.Equal(ConnectorFailureReason.Transient, progress.FailureReason);
        Assert.True(progress.RetryWorthwhile);
        Assert.Equal("the connection was reset", progress.Detail);
    }

    [Fact]
    public void A_failure_the_connector_says_not_to_repeat_is_not_repeated()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.Failed(
            ConnectorFailureReason.BrokerFormChanged,
            "the confirmation selector no longer matches",
            Retryable: false));

        Assert.False(progress.RetryWorthwhile);
    }

    /// <summary>
    /// A refusal is never retried, whatever the connector marked it.
    /// </summary>
    /// <remarks>
    /// The contract refuses that combination outright, so a connector should never produce
    /// it. This is the second place the rule holds, and the cheaper of the two to be wrong
    /// in: getting it wrong here means sending a demand back to a company that has already
    /// answered, in somebody's name.
    /// </remarks>
    [Fact]
    public void A_company_that_refused_is_not_asked_again()
    {
        var progress = RemovalOutcomes.For(new ConnectorResult.Failed(
            ConnectorFailureReason.Rejected,
            "the form answered: we do not hold data for this person",
            Retryable: true));

        Assert.False(progress.RetryWorthwhile);
        Assert.Equal(ConnectorFailureReason.Rejected, progress.FailureReason);
    }

    /// <summary>
    /// Every answer a connector can give moves the demand somewhere the lifecycle allows.
    /// </summary>
    /// <remarks>
    /// The check that makes the rest of this file mean something. A mapping that produced a
    /// state the table refuses would put a demand somewhere nothing else in the system
    /// believes in — and the handler would throw at the moment it tried, which is a company
    /// already contacted and a row that will not save.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryAnswer))]
    public void Every_answer_lands_somewhere_the_lifecycle_allows(ConnectorResult result)
    {
        var progress = RemovalOutcomes.For(result);

        Assert.True(
            RemovalLifecycle.IsAllowed(RemovalRequestStatus.Submitted, progress.RequestStatus),
            $"{result.GetType().Name} maps to {progress.RequestStatus}, which a submitted "
            + "demand cannot become.");
    }

    /// <summary>
    /// A retryable failure has somewhere to go back to as well.
    /// </summary>
    /// <remarks>
    /// The handler puts a demand worth retrying straight back into the queue rather than
    /// resting it in failed, so that edge has to exist too — and it carries the guard this
    /// codebase evaluates against the attempt budget.
    /// </remarks>
    [Fact]
    public void A_demand_worth_retrying_can_go_back_to_the_queue()
    {
        Assert.True(RemovalLifecycle.IsAllowed(
            RemovalRequestStatus.Failed,
            RemovalRequestStatus.Queued));

        Assert.Equal(
            RemovalGuard.RetriesRemaining,
            RemovalLifecycle.Find(RemovalRequestStatus.Failed, RemovalRequestStatus.Queued)!.Guard);
    }

    /// <summary>
    /// Nothing here is left without an answer.
    /// </summary>
    /// <remarks>
    /// Enumerated from the closed hierarchy rather than listed, so a sixth outcome added to
    /// the contract fails this test rather than throwing the first time a connector returns
    /// one.
    /// </remarks>
    [Fact]
    public void Every_case_of_the_result_type_is_covered_here()
    {
        var covered = Answers.Select(answer => answer.GetType()).ToHashSet();

        var declared = typeof(ConnectorResult).Assembly
            .GetTypes()
            .Where(type => type != typeof(ConnectorResult) && typeof(ConnectorResult).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal(declared.Length, covered.Count);
        Assert.All(declared, type => Assert.Contains(type, covered));
    }

    /// <summary>One of every answer the contract allows.</summary>
    private static readonly ConnectorResult[] Answers =
    [
        new ConnectorResult.Success("TICKET-1", null),
        new ConnectorResult.AlreadyClear(),
        new ConnectorResult.RequiresHumanInput(
            new HumanInputRequest(HumanInputKind.EmailConfirmation, "Follow the link.", null),
            Encoding.UTF8.GetBytes("draft")),
        new ConnectorResult.AwaitingBrokerResponse(Deadline, null),
        new ConnectorResult.Failed(ConnectorFailureReason.Transient, "reset", true),
    ];

    public static TheoryData<ConnectorResult> EveryAnswer()
    {
        var data = new TheoryData<ConnectorResult>();

        foreach (var answer in Answers)
        {
            data.Add(answer);
        }

        return data;
    }
}
