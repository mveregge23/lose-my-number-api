// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Vault;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Everything a listing's address is allowed to be: this finding, of this tenant.
/// </summary>
/// <remarks>
/// Two facts rather than the three a profile field carries, because there is no third: a
/// finding has one source reference, so there is no field name to tell apart.
/// </remarks>
public sealed record ExposureSourceBinding(Guid TenantId, Guid ExposureId);

/// <summary>
/// Encryption for the pointer to a broker's listing.
/// </summary>
/// <remarks>
/// <para>
/// The same envelope a profile field is written in, with a binding of its own. The prefix in
/// the associated data is what keeps the two apart: a ciphertext written as an exposure's
/// source cannot be read as a profile's names even if somebody moved the bytes and the ids
/// happened to line up, because the string that authenticates it names which kind of thing it
/// was written as.
/// </para>
/// <para>
/// <b>Bound to the exposure rather than to the scan that found it.</b> A finding outlives the
/// run — it is verified again, removed, and sometimes found again months later — and binding
/// it to the run would mean the bytes stop decrypting the moment anything else takes
/// ownership of the finding.
/// </para>
/// </remarks>
public static class ExposureSourceCipher
{
    /// <summary>The only format written today.</summary>
    public const byte Version = FieldCipher.Version;

    public static byte[] Encrypt(DataKey key, ExposureSourceBinding binding, string sourceRef)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);

        return FieldCipher.Encrypt(
            key,
            AssociatedData(binding),
            Encoding.UTF8.GetBytes(sourceRef));
    }

    /// <exception cref="CryptographicException">
    /// The ciphertext was altered, was encrypted under a different key, or was written for a
    /// different tenant or finding.
    /// </exception>
    public static string Decrypt(DataKey key, ExposureSourceBinding binding, ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return Encoding.UTF8.GetString(
            FieldCipher.Decrypt(key, AssociatedData(binding), ciphertext));
    }

    /// <summary>
    /// A listing's address, reduced to something that can be compared and indexed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a secret and not shown to anybody.</b> A digest of a URL is guessable for
    /// anyone who already has the URL, so this proves nothing and hides nothing — what it
    /// does is let the core store recognise a listing it has seen before without the vault
    /// being involved, which is what stops one listing becoming two findings and is what a
    /// later verification scan will match on.
    /// </para>
    /// <para>
    /// The absolute form of the URI, so that two spellings of one address — a trailing slash,
    /// a different case in the host — do not read as two listings.
    /// </para>
    /// </remarks>
    public static byte[] Digest(Uri sourceRef)
    {
        ArgumentNullException.ThrowIfNull(sourceRef);

        return SHA256.HashData(Encoding.UTF8.GetBytes(sourceRef.AbsoluteUri));
    }

    private static byte[] AssociatedData(ExposureSourceBinding binding) =>
        Encoding.UTF8.GetBytes(
            $"dbr/exposure-source/{Version}/{binding.TenantId:D}/{binding.ExposureId:D}");
}
