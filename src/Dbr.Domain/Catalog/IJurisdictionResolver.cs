// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Works out which regime, if any, governs a request against a broker, and by when it
/// has to be answered.
/// </summary>
/// <remarks>
/// <para>
/// A plain intersection: where the person lives, against the regimes somebody has
/// confirmed reach this company, for the kind of request being made. No overlap means
/// the broker's own target applies, labelled as the courtesy it is.
/// </para>
/// <para>
/// <b>It reads a region, never an identity.</b> A coarse region code is all this needs,
/// which is exactly why that code lives outside the encrypted store — resolving
/// jurisdiction happens on every request, and routing it through a key release for two
/// letters would hand out decryption rights to answer a question the ciphertext was never
/// hiding.
/// </para>
/// <para>
/// The answer is a value rather than something written down, because the row it belongs
/// on does not exist yet. When it does, the result is snapshotted onto the request at
/// creation and never recomputed: a statute corrected next year must not silently
/// reinterpret what somebody was told this year.
/// </para>
/// </remarks>
public interface IJurisdictionResolver
{
    /// <summary>
    /// The deadline for a request of this kind against this broker, for somebody living
    /// in this region.
    /// </summary>
    /// <param name="brokerId">The broker the request is aimed at.</param>
    /// <param name="residencyRegion">
    /// Where the person lives, as a coarse region code. <see langword="null"/> or
    /// unrecognised resolves to the broker's own target rather than failing — somebody
    /// who has not said where they live is not owed a worse answer than somebody in a
    /// state with no statute.
    /// </param>
    /// <param name="requestType">
    /// What is being asked. Part of the intersection rather than incidental to it: a
    /// regime's deadline for opting out of a sale says nothing about how long a deletion
    /// may take.
    /// </param>
    /// <param name="from">When the clock starts, which is when the broker receives it.</param>
    /// <exception cref="InvalidOperationException">
    /// No such broker. A deadline invented for a company the catalog has never heard of
    /// would be a number with nothing behind it.
    /// </exception>
    Task<DeadlineResolution> ResolveAsync(
        Guid brokerId,
        string? residencyRegion,
        LegalRequestType requestType,
        DateTimeOffset from,
        CancellationToken cancellationToken);
}
