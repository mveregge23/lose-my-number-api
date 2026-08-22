// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>
/// Asking for a scan, and reading what has been asked for before.
/// </summary>
/// <remarks>
/// <para>
/// Like the profile and consent services, every method acts for the tenant the current
/// scope was established for and none of them takes a tenant. A caller that could name
/// one could name the wrong one, and whose identity gets searched for is not a decision
/// to leave to an argument.
/// </para>
/// <para>
/// Nothing here dispatches. Requesting a scan records that it was asked for and leaves it
/// <see cref="ScanStatus.Queued"/>; the queue that picks it up does not exist yet, and
/// inventing an enqueue against a transport nobody has chosen would mean designing the
/// message shape against an imagined consumer.
/// </para>
/// </remarks>
public interface IScanService
{
    /// <summary>
    /// Records a manual scan request, after checking that it may run at all.
    /// </summary>
    /// <param name="profileId">
    /// Which identity to search for, or <see langword="null"/> for the tenant's own.
    /// Always one of the tenant's existing profiles — there is no way to pass an identity
    /// here, which is the guardrail rather than a check standing in for one.
    /// </param>
    /// <param name="brokerIds">
    /// The brokers to narrow to, or <see langword="null"/>/empty for the whole catalog.
    /// </param>
    Task<RequestScanResult> RequestAsync(
        Guid? profileId,
        IReadOnlyList<Guid>? brokerIds,
        CancellationToken cancellationToken);

    /// <summary>Every scan this tenant has asked for, newest first.</summary>
    Task<IReadOnlyList<Scan>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One scan and the brokers it was narrowed to.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when there is no such scan for this tenant — which covers
    /// both a scan that does not exist and one belonging to somebody else.
    /// </returns>
    Task<ScanDetail?> FindAsync(Guid scanId, CancellationToken cancellationToken);
}
