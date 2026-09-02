// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Vault;

/// <summary>
/// Where one finding was found, encrypted.
/// </summary>
/// <remarks>
/// <para>
/// <b>A URL, treated as identifying data, because it is one.</b> A people-search site's
/// profile address routinely spells out the name and the city of the person it is about, so
/// the link repeats the identity rather than referring to it. Keeping it beside the exposure
/// in the core store would put a name in the table the ordinary API path reads.
/// </para>
/// <para>
/// <b>Its own data key, unlike a profile's four fields.</b> Those are written and rewritten
/// together by one person editing their profile; findings arrive one at a time over months
/// and are purged one at a time as removals complete. A key shared across them would be kept
/// alive by the last surviving finding on behalf of everything already gone.
/// </para>
/// </remarks>
public class ExposureSource : ITenantScoped
{
    /// <summary>The finding this belongs to.</summary>
    public required Guid ExposureId { get; init; }

    /// <summary>The account it belongs to.</summary>
    /// <remarks>
    /// Carried here rather than reached through the exposure, because the boundary is
    /// enforced independently in each store — resolving the owner from the other one would
    /// mean this store trusting a table it deliberately cannot read.
    /// </remarks>
    public Guid TenantId { get; init; }

    /// <summary>This row's data key, as the key manager returned it.</summary>
    public required string WrappedDataKey { get; set; }

    /// <summary>The listing's address, under the key above.</summary>
    public required byte[] EncryptedSourceRef { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
