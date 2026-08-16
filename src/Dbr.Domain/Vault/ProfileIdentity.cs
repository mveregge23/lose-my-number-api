// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Vault;

/// <summary>
/// The identifying half of a privacy profile, as it sits in the vault store: ciphertext
/// and the wrapped key that undoes it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing outside the profile service should hold one of these. It is deliberately
/// unhelpful — every field is opaque bytes, and there is no method here that turns them
/// back into a name — so that code which acquires one by accident cannot do anything
/// with it.
/// </para>
/// <para>
/// It belongs to a tenant like any other row and is filtered like any other row. The
/// boundary is enforced separately in this store rather than inherited from the profile
/// row on the other side, because this store is meant to be movable to a database that
/// cannot see the other side at all.
/// </para>
/// </remarks>
public class ProfileIdentity : ITenantScoped
{
    /// <summary>
    /// The profile this belongs to. Also the primary key: one identity, one row.
    /// </summary>
    public Guid PrivacyProfileId { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>
    /// The key everything below was encrypted with, wrapped by the tenant's key in the
    /// key manager.
    /// </summary>
    /// <remarks>
    /// Stored exactly as the provider returned it and passed back unexamined. Reading
    /// meaning into it here would tie the schema to one key manager, which is the thing
    /// <see cref="IKeyManagementProvider"/> exists to avoid.
    /// </remarks>
    public required string WrappedDataKey { get; set; }

    public required byte[] EncryptedNames { get; set; }

    public required byte[] EncryptedAddresses { get; set; }

    public required byte[] EncryptedContacts { get; set; }

    /// <summary>
    /// <see langword="null"/> when the profile has no date of birth, which is ordinary.
    /// </summary>
    public byte[]? EncryptedDob { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
