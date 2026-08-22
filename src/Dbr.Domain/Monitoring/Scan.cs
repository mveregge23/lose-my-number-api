// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// One run of "ask these brokers what they hold about this identity".
/// </summary>
/// <remarks>
/// <para>
/// The identity is named by <see cref="PrivacyProfileId"/> and never by its contents.
/// That is the structural half of the guardrail: a scan cannot target anybody the tenant
/// has not already created a profile for and attested to, because there is nowhere on
/// this row — or on the request that creates it — to put a name.
/// </para>
/// <para>
/// The brokers in scope are rows in a separate table rather than a column here. No rows
/// means the whole catalog, which is a scope that has to be resolved when the scan runs
/// rather than frozen when it was asked for: a broker added in between is one the tenant
/// meant to include when they asked for all of them.
/// </para>
/// </remarks>
public class Scan : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account this run belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Which of the tenant's identities is being searched for.</summary>
    /// <remarks>
    /// The database enforces that this is one of the tenant's own, through a foreign key
    /// over the pair rather than over the id alone. A check in application code would be
    /// the kind of rule that holds until one path forgets it.
    /// </remarks>
    public required Guid PrivacyProfileId { get; init; }

    public required ScanTrigger Trigger { get; init; }

    public required ScanStatus Status { get; set; }

    /// <summary>When the run was asked for.</summary>
    /// <remarks>
    /// Distinct from <see cref="StartedAt"/> on purpose. A queued scan has been asked for
    /// and has not begun, and letting one column mean both would make every queued scan
    /// claim a start time it does not have — which is also the difference somebody needs
    /// in order to see that a run sat waiting rather than ran slowly.
    /// </remarks>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>When a worker picked it up, or <see langword="null"/> while queued.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// When it stopped, whether it finished or failed, or <see langword="null"/> while it
    /// has not.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
