// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>
/// The only way in and out of the vault store.
/// </summary>
/// <remarks>
/// <para>
/// Everything identifying passes through here: encrypted on the way in, decrypted on
/// the way out, and never handed to anything that could join it onto operational data.
/// The point of routing it all through one interface is that the number of places
/// holding plaintext is countable — this one, and whoever it answered.
/// </para>
/// <para>
/// Every method acts for the tenant the current scope was established for. There is no
/// tenant parameter on purpose: a caller that could name a tenant is a caller that
/// could name the wrong one, and the boundary would then depend on the argument being
/// right rather than on who is asking.
/// </para>
/// </remarks>
public interface IProfileService
{
    /// <summary>
    /// Creates a profile and stores its identifying fields.
    /// </summary>
    /// <remarks>
    /// The tenant's wrapping key is created if it does not exist yet, so signup does
    /// not have to remember to. A second <see cref="ProfileRelationship.Self"/> profile
    /// is refused by the database, which is where the rule belongs — there is exactly
    /// one and it is not this method's judgement call.
    /// </remarks>
    Task<PrivacyProfile> CreateAsync(
        ProfileRelationship relationship,
        string? residencyRegion,
        string attestationVersion,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken);

    /// <summary>
    /// The tenant's own profile, or <see langword="null"/> if it has none yet.
    /// </summary>
    Task<PrivacyProfile?> FindSelfAsync(CancellationToken cancellationToken);

    /// <summary>Every profile this tenant manages, its own included.</summary>
    Task<IReadOnlyList<PrivacyProfile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Decrypts one profile's identifying fields.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the profile does not exist or belongs to somebody
    /// else — those are the same answer here, because distinguishing them would confirm
    /// that an id belongs to another account.
    /// </returns>
    Task<ProfileIdentityFields?> ReadIdentityAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces one profile's identifying fields, re-encrypting all of them.
    /// </summary>
    /// <returns><see langword="false"/> if there is no such profile for this tenant.</returns>
    Task<bool> ReplaceIdentityAsync(
        Guid profileId,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken);
}
