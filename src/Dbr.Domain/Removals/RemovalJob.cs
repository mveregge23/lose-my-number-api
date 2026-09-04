// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;
using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Removals;

/// <summary>
/// One attempt at one removal request.
/// </summary>
/// <remarks>
/// <para>
/// Separate rows rather than a counter on the request, because what is worth keeping is
/// not how many times something was tried but what happened each time: which connector
/// ran, when, and whether it got anywhere. A retry that keeps failing the same way and one
/// failing differently each time are different problems, and a counter reports them
/// identically.
/// </para>
/// <para>
/// <b>There is no checkpoint column here yet, and that is deliberate.</b> §3 classes
/// <c>encryptedCheckpoint</c> as restricted-tier — it is a partly-filled form holding
/// somebody's name and address — which by the same section's rule puts it in the vault
/// store rather than on a row the ordinary path reads. It arrives with DBR‑039, which
/// builds the resume path it exists for. A nullable blob sitting here in the meantime is
/// exactly what gets filled in by whoever needs one without noticing which store they are
/// in.
/// </para>
/// </remarks>
public class RemovalJob : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this attempt was made for.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The demand this is an attempt at.</summary>
    public required Guid RemovalRequestId { get; init; }

    /// <summary>
    /// Which connector ran, by registry key or recipe reference.
    /// </summary>
    /// <remarks>
    /// Free text as far as the schema is concerned — the set of connectors is a build-time
    /// fact, not a database one — but shape-constrained, so it stays an identifier rather
    /// than becoming somewhere to put a sentence.
    /// </remarks>
    public required string ConnectorId { get; init; }

    public required RemovalJobStatus Status { get; set; }

    /// <summary>Which attempt this is, counting from one.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>When this attempt is due to run, or when it ran.</summary>
    public required DateTimeOffset RunAt { get; init; }

    /// <summary>
    /// When the next attempt should be made, or <see langword="null"/> if there is not
    /// going to be one.
    /// </summary>
    /// <remarks>
    /// On the job rather than the request because backoff is a property of what just
    /// happened: a rate-limited refusal and a malformed page both fail, and they should not
    /// be retried on the same schedule.
    /// </remarks>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>
    /// Why this attempt did not complete, or <see langword="null"/> when it ran.
    /// </summary>
    /// <remarks>
    /// The connector's own vocabulary, kept rather than collapsed into the status, because
    /// it is what decides whether trying again is worth anything: a timeout and a company
    /// declining the demand are both failures and only one of them is worth repeating.
    /// Without it this row records that an attempt failed and cannot say how, which is the
    /// same information a counter carries — and telling those apart is the reason these are
    /// separate rows in the first place.
    /// </remarks>
    public ConnectorFailureReason? FailureReason { get; set; }

    /// <summary>
    /// What happened, for whoever reads the row.
    /// </summary>
    /// <remarks>
    /// Never the identity the demand was made for and never the page's content — a status
    /// line, a selector that did not match, the name of a timeout that expired. Bounded by
    /// the column, so a connector that tries to put a page in here is truncated by the
    /// database rather than trusted.
    /// </remarks>
    public string? Detail { get; set; }
}
