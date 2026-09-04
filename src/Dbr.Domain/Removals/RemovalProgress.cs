// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;

namespace Dbr.Domain.Removals;

/// <summary>
/// What one connector's answer does to the attempt and to the demand behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The job status and the request status are not the same question.</b> A job records
/// whether the attempt ran and reported something; the request records where the demand
/// stands. A connector that reaches a step no script completes has <i>succeeded</i> as an
/// attempt — it ran, it got somewhere, it said what it needs — while the demand it belongs
/// to is now parked waiting for a person. Reading one off the other would make a stopped
/// demand look like a broken worker.
/// </para>
/// <para>
/// <see cref="RetryWorthwhile"/> is the connector's judgement carried forward rather than
/// this type's own. Whether there is any budget left for another attempt is a fact about
/// the request, which only the caller can see; whether another attempt could possibly help
/// is a fact about what just happened, which only the connector saw.
/// </para>
/// </remarks>
/// <param name="Detail">
/// What happened, for whoever reads the row. Never the identity the demand was made for
/// and never the page's content.
/// </param>
public sealed record RemovalProgress(
    RemovalJobStatus JobStatus,
    RemovalRequestStatus RequestStatus,
    ConnectorFailureReason? FailureReason,
    bool RetryWorthwhile,
    string Detail);

/// <summary>
/// Turns what a connector answered into where the demand now stands.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over a closed result type, which is what makes it worth having on its
/// own. The dispatcher's other work — claiming a row, writing an attempt, putting a message
/// in a lane — needs a database to say anything about; this is the part that decides what a
/// demand's history looks like, and it can be checked exhaustively without one.
/// </para>
/// <para>
/// Every branch here lands on a transition the lifecycle allows from
/// <see cref="RemovalRequestStatus.Submitted"/>, which is where a dispatched demand is when
/// its connector answers. That agreement is asserted rather than assumed: a result mapping
/// to a state the table refuses would be a demand this code could put somewhere the rest of
/// the system does not believe in.
/// </para>
/// </remarks>
public static class RemovalOutcomes
{
    /// <summary>Where one answer leaves the attempt and the demand.</summary>
    public static RemovalProgress For(ConnectorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result switch
        {
            // The demand is in and the clock is running. Not Removed: a company that
            // accepted a request has not yet honoured it, and only a verification scan can
            // say whether the listing is actually gone.
            ConnectorResult.Success success => new RemovalProgress(
                RemovalJobStatus.Succeeded,
                RemovalRequestStatus.AwaitingBrokerResponse,
                FailureReason: null,
                RetryWorthwhile: false,
                success.ReceiptRef is { } receipt
                    ? $"The company accepted the demand and issued receipt {receipt}."
                    : "The company accepted the demand and issued no receipt."),

            // It looked and there was nothing to remove, so there is nothing to wait for.
            // The one branch that reaches Removed without a verification scan, and the
            // reason the lifecycle carries an edge that §5's diagram does not draw.
            ConnectorResult.AlreadyClear => new RemovalProgress(
                RemovalJobStatus.Succeeded,
                RemovalRequestStatus.Removed,
                FailureReason: null,
                RetryWorthwhile: false,
                "There was nothing left to remove, so no demand was sent."),

            // The attempt ran and got as far as anything can without a person. Succeeded,
            // because it did what it could and said what it needs.
            ConnectorResult.RequiresHumanInput ask => new RemovalProgress(
                RemovalJobStatus.Succeeded,
                RemovalRequestStatus.RequiresHumanInput,
                FailureReason: null,
                RetryWorthwhile: false,
                $"The connector stopped and needs a person: {ask.Ask.Kind}."),

            // Sent, and the company said when it would answer. Distinct from Success only
            // in that the deadline came from the company rather than from the catalog.
            ConnectorResult.AwaitingBrokerResponse waiting => new RemovalProgress(
                RemovalJobStatus.Succeeded,
                RemovalRequestStatus.AwaitingBrokerResponse,
                FailureReason: null,
                RetryWorthwhile: false,
                $"The demand is in and an answer is due by {waiting.Deadline:u}."),

            ConnectorResult.Failed failed => new RemovalProgress(
                RemovalJobStatus.Failed,
                RemovalRequestStatus.Failed,
                failed.Reason,

                // A refusal is never retried however the connector marked it. The contract
                // refuses that combination outright, so this is the second place the rule
                // holds rather than the first — and the cheaper of the two to be wrong in.
                failed.Reason != ConnectorFailureReason.Rejected && failed.Retryable,
                failed.Detail),

            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result,
                "Unmapped connector result. A way for a connector to answer that a demand "
                + "cannot be moved by is one that would leave a request sitting in the state "
                + "it was dispatched in, with nothing recording why."),
        };
    }
}
