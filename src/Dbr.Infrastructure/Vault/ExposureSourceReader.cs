// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Opens the pointer to a listing, for the one caller allowed to: a spent grant.
/// </summary>
/// <remarks>
/// <para>
/// The read half of what <see cref="FindingReporter"/> writes. A finding's address is a
/// copy of somebody's identity — a people-search profile URL routinely spells out the name
/// and the city — so it lives in the vault under its own data key, and the ordinary API
/// path that lists findings never opens it. This does, and it is reached from exactly one
/// place: redeeming a grant minted for an attempt at a removal whose demand cites the
/// finding. The company being asked to take a listing down is the one party there is no
/// point withholding its address from.
/// </para>
/// <para>
/// Bound to the tenant and the exposure it was written for, so a vault row copied under
/// another finding fails to open rather than pointing one person's demand at another
/// person's listing.
/// </para>
/// </remarks>
public sealed class ExposureSourceReader(VaultDbContext vault, IKeyManagementProvider keys)
{
    /// <returns>
    /// The listing, or <see langword="null"/> when the vault holds no row for the exposure —
    /// which is a finding whose address has since been purged, and is not an error.
    /// </returns>
    public async Task<Uri?> ReadAsync(Guid tenantId, Guid exposureId, CancellationToken cancellationToken)
    {
        var stored = await vault.Set<ExposureSource>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ExposureId == exposureId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        using var key = await keys
            .UnwrapDataKeyAsync(tenantId, stored.WrappedDataKey, cancellationToken)
            .ConfigureAwait(false);

        var sourceRef = ExposureSourceCipher.Decrypt(
            key,
            new ExposureSourceBinding(tenantId, exposureId),
            stored.EncryptedSourceRef);

        return new Uri(sourceRef, UriKind.Absolute);
    }
}
