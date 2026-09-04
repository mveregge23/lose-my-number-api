// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;

namespace Dbr.Domain.Removals;

/// <summary>
/// The one spelling of each removal enum: what a column holds, and what a client sends
/// and reads back.
/// </summary>
/// <remarks>
/// The same arrangement the catalog and monitoring vocabularies describe. Two of these
/// have members that are more than one word, so the lower-cased member name would give
/// <c>semiautomated</c> and <c>requireshumaninput</c> — values no check constraint accepts
/// and no client sends. Spelled out rather than derived.
/// </remarks>
public static class RemovalVocabulary
{
    public static string ToWire(RemovalStrategy strategy) => strategy switch
    {
        RemovalStrategy.Automated => "automated",
        RemovalStrategy.SemiAutomated => "semi_automated",
        RemovalStrategy.ManualEmail => "manual_email",
        _ => throw new ArgumentOutOfRangeException(
            nameof(strategy),
            strategy,
            "Unmapped removal strategy. Adding one means a migration widening the check "
            + "constraint on removal_request.strategy as well."),
    };

    public static RemovalStrategy? ParseStrategy(string? value) => value switch
    {
        "automated" => RemovalStrategy.Automated,
        "semi_automated" => RemovalStrategy.SemiAutomated,
        "manual_email" => RemovalStrategy.ManualEmail,
        _ => null,
    };

    public static string ToWire(RemovalRequestStatus status) => status switch
    {
        RemovalRequestStatus.Queued => "queued",
        RemovalRequestStatus.Submitted => "submitted",
        RemovalRequestStatus.RequiresHumanInput => "requires_human_input",
        RemovalRequestStatus.AwaitingBrokerResponse => "awaiting_broker_response",
        RemovalRequestStatus.Removed => "removed",
        RemovalRequestStatus.Reappeared => "reappeared",
        RemovalRequestStatus.Failed => "failed",
        RemovalRequestStatus.Expired => "expired",
        RemovalRequestStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unmapped request status. Adding one means a migration widening the check "
            + "constraint on removal_request.status as well."),
    };

    public static RemovalRequestStatus? ParseRequestStatus(string? value) => value switch
    {
        "queued" => RemovalRequestStatus.Queued,
        "submitted" => RemovalRequestStatus.Submitted,
        "requires_human_input" => RemovalRequestStatus.RequiresHumanInput,
        "awaiting_broker_response" => RemovalRequestStatus.AwaitingBrokerResponse,
        "removed" => RemovalRequestStatus.Removed,
        "reappeared" => RemovalRequestStatus.Reappeared,
        "failed" => RemovalRequestStatus.Failed,
        "expired" => RemovalRequestStatus.Expired,
        "cancelled" => RemovalRequestStatus.Cancelled,
        _ => null,
    };

    public static string ToWire(RemovalJobStatus status) => status switch
    {
        RemovalJobStatus.Pending => "pending",
        RemovalJobStatus.Running => "running",
        RemovalJobStatus.Succeeded => "succeeded",
        RemovalJobStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unmapped job status. Adding one means a migration widening the check "
            + "constraint on removal_job.status as well."),
    };

    public static RemovalJobStatus? ParseJobStatus(string? value) => value switch
    {
        "pending" => RemovalJobStatus.Pending,
        "running" => RemovalJobStatus.Running,
        "succeeded" => RemovalJobStatus.Succeeded,
        "failed" => RemovalJobStatus.Failed,
        _ => null,
    };

    /// <summary>
    /// How a connector's account of a failure is spelled on an attempt.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the connector contract, because this is a storage spelling
    /// and the contract has none — nothing in that namespace knows there is a column. The
    /// same arrangement every other vocabulary in this codebase describes.
    /// </remarks>
    public static string ToWire(ConnectorFailureReason reason) => reason switch
    {
        ConnectorFailureReason.Transient => "transient",
        ConnectorFailureReason.RateLimited => "rate_limited",
        ConnectorFailureReason.BrokerFormChanged => "broker_form_changed",
        ConnectorFailureReason.Rejected => "rejected",
        ConnectorFailureReason.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "Unmapped connector failure. Adding one means a migration widening the check "
            + "constraint on removal_job.failure_reason as well."),
    };

    public static ConnectorFailureReason? ParseFailureReason(string? value) => value switch
    {
        "transient" => ConnectorFailureReason.Transient,
        "rate_limited" => ConnectorFailureReason.RateLimited,
        "broker_form_changed" => ConnectorFailureReason.BrokerFormChanged,
        "rejected" => ConnectorFailureReason.Rejected,
        "unsupported" => ConnectorFailureReason.Unsupported,
        _ => null,
    };
}
