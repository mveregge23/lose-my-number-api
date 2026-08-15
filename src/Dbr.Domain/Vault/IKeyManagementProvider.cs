// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Vault;

/// <summary>
/// A freshly generated data key: the plaintext to encrypt with now, and the wrapped
/// form to keep.
/// </summary>
/// <param name="Key">
/// Use it and dispose it. Nothing should store this, and nothing should hold it across
/// a request.
/// </param>
/// <param name="Wrapped">
/// The same key, encrypted by a key that never leaves the key manager. Opaque on
/// purpose: it is whatever the provider produced, stored verbatim and handed back
/// unexamined, so a different provider's format is a configuration change rather than
/// a migration.
/// </param>
public sealed record GeneratedDataKey(DataKey Key, string Wrapped);

/// <summary>
/// Where the keys that protect identifying data live.
/// </summary>
/// <remarks>
/// <para>
/// This is the interface the core owns and the vault service is written against;
/// OpenBao's Transit engine is the implementation that ships. Swapping in a managed
/// HSM is meant to be a registration in a composition root rather than a change to
/// anything that encrypts, which only holds if nothing here leaks the shape of one
/// particular product.
/// </para>
/// <para>
/// <b>Envelope encryption, and why the plaintext key is here at all.</b> Sending every
/// field to the key manager would mean a network round trip per field and a service
/// that has seen every value it protects. Instead each tenant gets a data key, that
/// key encrypts their data locally, and only the data key is wrapped by something the
/// key manager holds. A dump of the database is then ciphertext plus wrapped keys, and
/// neither half is worth anything without the key manager.
/// </para>
/// <para>
/// <b>The wrapping key is per tenant, not one master key for everyone.</b> That is what
/// makes erasure real: destroying one tenant's wrapping key renders every data key
/// wrapped by it permanently unreadable — including copies sitting in a backup nobody
/// can reach to delete — and touches no other tenant. With a single master key,
/// erasure would depend on every copy of a row being found and removed, which is not a
/// promise a backup policy can keep.
/// </para>
/// </remarks>
public interface IKeyManagementProvider
{
    /// <summary>
    /// Makes sure a tenant has a wrapping key, creating it if it does not exist.
    /// </summary>
    /// <remarks>
    /// Idempotent, so it can be called on a path that may run more than once without
    /// the caller having to remember whether it already has.
    /// </remarks>
    Task EnsureTenantKeyAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Generates a new data key for a tenant.
    /// </summary>
    /// <remarks>
    /// The plaintext half is returned exactly once and never again: only the wrapped
    /// form is storable, and getting the key back means asking for it to be unwrapped.
    /// </remarks>
    Task<GeneratedDataKey> GenerateDataKeyAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Turns a stored wrapped key back into one that can decrypt.
    /// </summary>
    /// <exception cref="KeyManagementException">
    /// The key could not be unwrapped — the tenant's wrapping key is gone, or the
    /// wrapped value does not belong to it.
    /// </exception>
    Task<DataKey> UnwrapDataKeyAsync(
        Guid tenantId,
        string wrappedKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Destroys a tenant's wrapping key, making everything it wrapped unreadable
    /// forever.
    /// </summary>
    /// <remarks>
    /// The cryptographic half of deleting an account. There is no undo and no support
    /// path that recovers from it — that is the point, and it is why the account
    /// deletion flow rather than any ordinary code path is what calls this.
    /// </remarks>
    Task DestroyTenantKeyAsync(Guid tenantId, CancellationToken cancellationToken);
}
