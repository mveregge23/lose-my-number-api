// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Monitoring;

/// <summary>
/// Reading what the scans found, and saying which of it is not you.
/// </summary>
/// <remarks>
/// Like the scan service, every method acts for the tenant the current scope was
/// established for and none of them takes a tenant. What was found about somebody is not
/// a question that should be answerable by naming them in an argument.
/// </remarks>
public interface IExposureService
{
    /// <summary>Findings for this tenant, newest first.</summary>
    Task<IReadOnlyList<ExposureListing>> ListAsync(
        ExposureFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// One finding.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when there is no such finding for this tenant — which
    /// covers both one that does not exist and one belonging to somebody else.
    /// </returns>
    Task<ExposureListing?> FindAsync(Guid exposureId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a finding as not being this person.
    /// </summary>
    /// <remarks>
    /// The one judgement in this system that only the tenant can make. Nothing is sent in
    /// somebody's name over a listing they have said is somebody else, so this is a
    /// decision with consequences rather than a display preference.
    /// </remarks>
    Task<DismissExposureResult> DismissAsync(Guid exposureId, CancellationToken cancellationToken);
}
