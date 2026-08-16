// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The vault-fronting profile service: the one place plaintext identity fields exist.
/// </summary>
/// <remarks>
/// <para>
/// A profile is stored in two halves. What routing needs — whose it is, what
/// relationship is claimed, roughly which jurisdiction applies — is an ordinary row in
/// the core store. What identifies a person is a row in the vault store, encrypted
/// field by field under a data key that only the key manager can unwrap. This class is
/// what holds the two together, and it is deliberately the only thing that does.
/// </para>
/// <para>
/// <b>There is no transaction spanning the two stores, and there should not be.</b> They
/// are separate connections today and separate databases eventually, so anything
/// written to depend on their atomicity would be a promise that expires on the day of
/// the move. What the code does instead is order the writes so the surviving failure is
/// the harmless one: the vault row goes first, so a failure afterwards leaves an
/// encrypted row nothing references — unreadable by any query path, and unreadable
/// full stop once the tenant's key is destroyed. The other order would leave a profile
/// that exists, is listed, and has no identity behind it.
/// </para>
/// </remarks>
public sealed class ProfileService(
    DbrDbContext core,
    VaultDbContext vault,
    IKeyManagementProvider keys,
    ITenantContext tenantContext)
    : IProfileService
{
    private static readonly JsonSerializerOptions FieldFormat = new()
    {
        // Enum members by name. The numbers shift the day somebody inserts a value in
        // the middle of an enum, and the ciphertext written last year is not going to be
        // re-read for a review.
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<PrivacyProfile> CreateAsync(
        ProfileRelationship relationship,
        string? residencyRegion,
        string attestationVersion,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(attestationVersion);

        var tenantId = RequireTenant();
        var profileId = Guid.NewGuid();

        // Idempotent, so signup does not have to know whether this is the tenant's
        // first profile.
        await keys.EnsureTenantKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        vault.Set<ProfileIdentity>().Add(
            await EncryptAsync(tenantId, profileId, fields, now, cancellationToken).ConfigureAwait(false));

        await vault.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var profile = new PrivacyProfile
        {
            Id = profileId,
            TenantId = tenantId,
            RelationshipType = relationship,
            ResidencyRegion = residencyRegion,
            AttestedAt = now,
            AttestationVersion = attestationVersion,
            CreatedAt = now,
        };

        core.Set<PrivacyProfile>().Add(profile);
        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return profile;
    }

    public async Task<PrivacyProfile?> FindSelfAsync(CancellationToken cancellationToken) =>
        await core.Set<PrivacyProfile>()
            .FirstOrDefaultAsync(
                profile => profile.RelationshipType == ProfileRelationship.Self,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PrivacyProfile>> ListAsync(CancellationToken cancellationToken) =>
        await core.Set<PrivacyProfile>()
            .OrderBy(profile => profile.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<ProfileIdentityFields?> ReadIdentityAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();

        // The vault store alone, without consulting the core row first. Both stores
        // enforce the tenant boundary independently, so asking twice would add a query
        // and no safety — and a read of somebody's identity fields that does not need
        // the operational store is a read that keeps working when the vault moves.
        var identity = await vault.Set<ProfileIdentity>()
            .FirstOrDefaultAsync(row => row.PrivacyProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);

        if (identity is null)
        {
            return null;
        }

        using var key = await keys
            .UnwrapDataKeyAsync(tenantId, identity.WrappedDataKey, cancellationToken)
            .ConfigureAwait(false);

        return new ProfileIdentityFields(
            Read<List<string>>(key, tenantId, profileId, ProfileField.Names, identity.EncryptedNames),
            Read<List<ProfileAddress>>(key, tenantId, profileId, ProfileField.Addresses, identity.EncryptedAddresses),
            Read<List<ProfileContact>>(key, tenantId, profileId, ProfileField.Contacts, identity.EncryptedContacts),
            identity.EncryptedDob is { } dob
                ? Read<DateOnly>(key, tenantId, profileId, ProfileField.DateOfBirth, dob)
                : null);
    }

    public async Task<bool> ReplaceIdentityAsync(
        Guid profileId,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var tenantId = RequireTenant();

        var existing = await vault.Set<ProfileIdentity>()
            .FirstOrDefaultAsync(row => row.PrivacyProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        // A new data key rather than the one already stored. Every field is being
        // rewritten anyway, so nothing needs the old key — and not unwrapping it means
        // the old plaintext key never exists in this process at all. Generating one
        // costs the same single call that unwrapping would have.
        var replacement = await EncryptAsync(
            tenantId, profileId, fields, existing.CreatedAt, cancellationToken).ConfigureAwait(false);

        existing.WrappedDataKey = replacement.WrappedDataKey;
        existing.EncryptedNames = replacement.EncryptedNames;
        existing.EncryptedAddresses = replacement.EncryptedAddresses;
        existing.EncryptedContacts = replacement.EncryptedContacts;
        existing.EncryptedDob = replacement.EncryptedDob;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await vault.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<ProfileIdentity> EncryptAsync(
        Guid tenantId,
        Guid profileId,
        ProfileIdentityFields fields,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var generated = await keys.GenerateDataKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        // Disposed as soon as the four fields are written, which is the whole of its
        // useful life. Holding it any longer only widens the window in which a crash
        // dump contains something that decrypts an identity.
        using var key = generated.Key;

        return new ProfileIdentity
        {
            PrivacyProfileId = profileId,
            TenantId = tenantId,
            WrappedDataKey = generated.Wrapped,
            EncryptedNames = Write(key, tenantId, profileId, ProfileField.Names, fields.Names),
            EncryptedAddresses = Write(key, tenantId, profileId, ProfileField.Addresses, fields.Addresses),
            EncryptedContacts = Write(key, tenantId, profileId, ProfileField.Contacts, fields.Contacts),
            EncryptedDob = fields.DateOfBirth is { } dob
                ? Write(key, tenantId, profileId, ProfileField.DateOfBirth, dob)
                : null,
            CreatedAt = createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static byte[] Write<TValue>(
        DataKey key,
        Guid tenantId,
        Guid profileId,
        ProfileField field,
        TValue value) =>
        ProfileCipher.Encrypt(
            key,
            new ProfileFieldBinding(tenantId, profileId, field),
            JsonSerializer.SerializeToUtf8Bytes(value, FieldFormat));

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The stored bytes are not what this profile's field held. Deliberately not caught
    /// and turned into an empty value: a field that will not decrypt means the row was
    /// altered, restored from somewhere it does not belong, or written under a key that
    /// no longer applies, and each of those wants a person to look rather than a silent
    /// blank in a removal request.
    /// </exception>
    private static TValue Read<TValue>(
        DataKey key,
        Guid tenantId,
        Guid profileId,
        ProfileField field,
        byte[] stored) =>
        JsonSerializer.Deserialize<TValue>(
            ProfileCipher.Decrypt(key, new ProfileFieldBinding(tenantId, profileId, field), stored),
            FieldFormat)!;

    /// <exception cref="InvalidOperationException">
    /// The scope is not acting for a tenant. Every database read here would return
    /// nothing and every write would be refused by the policy, so the useful failure is
    /// the one that names the cause.
    /// </exception>
    private Guid RequireTenant() =>
        tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "The profile service was asked to work without a tenant. Identity fields "
            + "belong to exactly one account, and a scope that never established one has "
            + "no account to act for.");
}
