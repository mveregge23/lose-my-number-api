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
/// <b>Every edit is a read-modify-write, and that is not an implementation detail.</b>
/// The fields are encrypted as a whole under a key replaced on every save, so adding one
/// address means decrypting the rest, changing the part that changed, and writing it all
/// back. Two edits overlapping would not merge — so the row carries a concurrency token
/// and the second writer is refused rather than quietly overwriting what the first
/// added.
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
        var encrypted = await EncryptAsync(tenantId, profileId, fields, cancellationToken)
            .ConfigureAwait(false);

        vault.Set<ProfileIdentity>().Add(new ProfileIdentity
        {
            PrivacyProfileId = profileId,
            TenantId = tenantId,
            WrappedDataKey = encrypted.WrappedDataKey,
            EncryptedNames = encrypted.Names,
            EncryptedAddresses = encrypted.Addresses,
            EncryptedContacts = encrypted.Contacts,
            EncryptedDob = encrypted.DateOfBirth,
            CreatedAt = now,
            UpdatedAt = now,
        });

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

        var identity = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);

        return identity is null
            ? null
            : await DecryptAsync(identity, tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReplaceIdentityAsync(
        Guid profileId,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var tenantId = RequireTenant();

        var identity = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (identity is null)
        {
            return false;
        }

        // Nothing is read first: every field is being replaced, so the old plaintext is
        // not needed and the old key never has to be unwrapped.
        await SaveAsync(identity, tenantId, fields, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<bool> ReplaceDetailsAsync(
        Guid profileId,
        ProfileDetails details,
        string? residencyRegion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);

        var tenantId = RequireTenant();

        var profile = await core.Set<PrivacyProfile>()
            .FirstOrDefaultAsync(row => row.Id == profileId, cancellationToken)
            .ConfigureAwait(false);

        var identity = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (profile is null || identity is null)
        {
            return false;
        }

        // The addresses have to be decrypted in order to be written back unchanged.
        // Unavoidable while the fields share a data key that is replaced on every save,
        // and the alternative — leaving the old ciphertext in place beside newly
        // encrypted fields — would mean a row whose columns are under two different
        // keys, which is a state nothing else in the design has to reason about.
        var current = await DecryptAsync(identity, tenantId, cancellationToken).ConfigureAwait(false);

        await SaveAsync(
            identity,
            tenantId,
            current with
            {
                Names = details.Names,
                Contacts = details.Contacts,
                DateOfBirth = details.DateOfBirth,
            },
            cancellationToken).ConfigureAwait(false);

        profile.ResidencyRegion = residencyRegion;
        await core.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<AddAddressResult> AddAddressAsync(
        Guid profileId,
        ProfileAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var tenantId = RequireTenant();

        var identity = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (identity is null)
        {
            return AddAddressResult.Failed(AddAddressOutcome.ProfileNotFound);
        }

        var current = await DecryptAsync(identity, tenantId, cancellationToken).ConfigureAwait(false);

        // The only limit that cannot be checked at the edge: how many are already there
        // is a question only something holding the key can answer.
        if (current.Addresses.Count >= ProfileLimits.MaxAddresses)
        {
            return AddAddressResult.Failed(AddAddressOutcome.TooMany);
        }

        // The id is assigned here rather than accepted from the caller.
        var stored = address with { Id = Guid.NewGuid() };

        await SaveAsync(
            identity,
            tenantId,
            current with { Addresses = [.. current.Addresses, stored] },
            cancellationToken).ConfigureAwait(false);

        return new AddAddressResult(AddAddressOutcome.Added, stored);
    }

    public async Task<bool> RemoveAddressAsync(
        Guid profileId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();

        var identity = await LoadAsync(profileId, cancellationToken).ConfigureAwait(false);

        if (identity is null)
        {
            return false;
        }

        var current = await DecryptAsync(identity, tenantId, cancellationToken).ConfigureAwait(false);
        var remaining = current.Addresses.Where(existing => existing.Id != addressId).ToList();

        if (remaining.Count == current.Addresses.Count)
        {
            // Nothing to remove, and nothing written: a save here would rotate the data
            // key and rewrite every field to record that a request asked for something
            // that was not there.
            return false;
        }

        await SaveAsync(
            identity,
            tenantId,
            current with { Addresses = remaining },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<ProfileIdentity?> LoadAsync(Guid profileId, CancellationToken cancellationToken) =>
        // The vault store alone, without consulting the core row first. Both stores
        // enforce the tenant boundary independently, so asking twice would add a query
        // and no safety — and a read of somebody's identity fields that does not need
        // the operational store is a read that keeps working when the vault moves.
        await vault.Set<ProfileIdentity>()
            .FirstOrDefaultAsync(row => row.PrivacyProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);

    private async Task<ProfileIdentityFields> DecryptAsync(
        ProfileIdentity identity,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var profileId = identity.PrivacyProfileId;

        using var key = await keys
            .UnwrapDataKeyAsync(tenantId, identity.WrappedDataKey, cancellationToken)
            .ConfigureAwait(false);

        return new ProfileIdentityFields(
            Read<List<string>>(key, tenantId, profileId, IdentityField.Names, identity.EncryptedNames),
            Read<List<ProfileAddress>>(key, tenantId, profileId, IdentityField.Addresses, identity.EncryptedAddresses),
            Read<List<ProfileContact>>(key, tenantId, profileId, IdentityField.Contacts, identity.EncryptedContacts),
            identity.EncryptedDob is { } dob
                ? Read<DateOnly>(key, tenantId, profileId, IdentityField.DateOfBirth, dob)
                : null);
    }

    /// <exception cref="ProfileChangedException">
    /// The row was written by somebody else after it was loaded here.
    /// </exception>
    private async Task SaveAsync(
        ProfileIdentity identity,
        Guid tenantId,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken)
    {
        // A new data key rather than the one already stored. Every field is being
        // rewritten anyway, so nothing needs the old key — and not unwrapping it means
        // the old plaintext key never exists in this process at all. Generating one
        // costs the same single call that unwrapping would have.
        var encrypted = await EncryptAsync(tenantId, identity.PrivacyProfileId, fields, cancellationToken)
            .ConfigureAwait(false);

        identity.WrappedDataKey = encrypted.WrappedDataKey;
        identity.EncryptedNames = encrypted.Names;
        identity.EncryptedAddresses = encrypted.Addresses;
        identity.EncryptedContacts = encrypted.Contacts;
        identity.EncryptedDob = encrypted.DateOfBirth;
        identity.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await vault.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Translated rather than surfaced, because the caller is being told
            // something about profiles, not about EF: read it again and reapply.
            throw new ProfileChangedException(
                "This profile was changed by another request. Read it again and reapply the change.",
                exception);
        }
    }

    private async Task<EncryptedIdentity> EncryptAsync(
        Guid tenantId,
        Guid profileId,
        ProfileIdentityFields fields,
        CancellationToken cancellationToken)
    {
        var generated = await keys.GenerateDataKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        // Disposed as soon as the four fields are written, which is the whole of its
        // useful life. Holding it any longer only widens the window in which a crash
        // dump contains something that decrypts an identity.
        using var key = generated.Key;

        return new EncryptedIdentity(
            generated.Wrapped,
            Write(key, tenantId, profileId, IdentityField.Names, fields.Names),
            Write(key, tenantId, profileId, IdentityField.Addresses, fields.Addresses),
            Write(key, tenantId, profileId, IdentityField.Contacts, fields.Contacts),
            fields.DateOfBirth is { } dob
                ? Write(key, tenantId, profileId, IdentityField.DateOfBirth, dob)
                : null);
    }

    private static byte[] Write<TValue>(
        DataKey key,
        Guid tenantId,
        Guid profileId,
        IdentityField field,
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
        IdentityField field,
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

    /// <summary>One profile's fields, as columns, under a key that has just been minted.</summary>
    private sealed record EncryptedIdentity(
        string WrappedDataKey,
        byte[] Names,
        byte[] Addresses,
        byte[] Contacts,
        byte[]? DateOfBirth);
}
