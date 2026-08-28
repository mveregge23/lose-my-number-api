// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Everything a ciphertext is allowed to be: this field, of this profile, of this
/// tenant.
/// </summary>
/// <remarks>
/// <para>
/// Passed to both halves of the cipher and mixed into the authentication tag, so the
/// three facts are checked cryptographically rather than assumed. See
/// <see cref="ProfileCipher"/> for what that buys.
/// </para>
/// <para>
/// <b>The field's member name is stored data, not just a label.</b> It goes into the
/// associated data as text, so every ciphertext ever written carries the spelling that
/// was current when it was written. Renaming a member of
/// <see cref="IdentityField"/> would not fail to compile and would not fail a round
/// trip in one process &mdash; it would fail to decrypt everything already in the
/// database, at the moment somebody asked for their own name back. A test pins the
/// exact bytes so that a rename is a red build instead.
/// </para>
/// </remarks>
public sealed record ProfileFieldBinding(Guid TenantId, Guid PrivacyProfileId, IdentityField Field);

/// <summary>
/// Field-level encryption under a profile's data key.
/// </summary>
/// <remarks>
/// <para>
/// AES-256-GCM: authenticated, so a modified ciphertext fails to decrypt rather than
/// producing plausible garbage, and a fresh random nonce per encryption, so writing the
/// same name twice does not produce the same bytes twice.
/// </para>
/// <para>
/// <b>The binding is the part worth understanding.</b> GCM's associated data is
/// authenticated but not stored, and this passes the tenant, the profile and the field
/// name through it. The effect is that a ciphertext only decrypts in the exact position
/// it was written to: copying the encrypted names of one profile over another's — in
/// the database, in a backup restore, in a mistaken UPDATE — produces a decryption
/// failure instead of one person's identity appearing under someone else's account.
/// Row-level security already stops a query from reaching across accounts; this stops
/// the bytes themselves from being meaningful anywhere but where they belong.
/// </para>
/// <para>
/// The stored form is a version byte, the nonce, then the ciphertext and its tag. The
/// version is there because the alternative to being able to change algorithms is
/// re-encrypting everything under a format nobody can identify.
/// </para>
/// </remarks>
public static class ProfileCipher
{
    /// <summary>The only format written today.</summary>
    public const byte Version = 1;

    private const int NonceSize = 12;

    private const int TagSize = 16;

    public static byte[] Encrypt(DataKey key, ProfileFieldBinding binding, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(binding);

        var output = new byte[1 + NonceSize + plaintext.Length + TagSize];
        output[0] = Version;

        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key.Material, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(1 + NonceSize, plaintext.Length),
            output.AsSpan(1 + NonceSize + plaintext.Length, TagSize),
            AssociatedData(binding));

        return output;
    }

    /// <exception cref="CryptographicException">
    /// The ciphertext was altered, was encrypted under a different key, or was written
    /// for a different tenant, profile or field.
    /// </exception>
    public static byte[] Decrypt(DataKey key, ProfileFieldBinding binding, ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(binding);

        if (ciphertext.Length < 1 + NonceSize + TagSize)
        {
            throw new CryptographicException(
                "Stored value is too short to be a ciphertext this cipher produced.");
        }

        if (ciphertext[0] != Version)
        {
            throw new CryptographicException(
                $"Stored value carries format version {ciphertext[0]}, which this build cannot "
                + "read. A field written by a newer version is not something to guess at.");
        }

        var payloadLength = ciphertext.Length - 1 - NonceSize - TagSize;
        var plaintext = new byte[payloadLength];

        using var aes = new AesGcm(key.Material, TagSize);
        aes.Decrypt(
            ciphertext.Slice(1, NonceSize),
            ciphertext.Slice(1 + NonceSize, payloadLength),
            ciphertext.Slice(1 + NonceSize + payloadLength, TagSize),
            plaintext,
            AssociatedData(binding));

        return plaintext;
    }

    /// <summary>
    /// The three facts a ciphertext is bound to, in a form that cannot be confused
    /// between fields — separators no component can contain, since two of them are
    /// GUIDs and the third is an enum name.
    /// </summary>
    private static byte[] AssociatedData(ProfileFieldBinding binding) =>
        Encoding.UTF8.GetBytes(
            $"dbr/profile-identity/{Version}/{binding.TenantId:D}/{binding.PrivacyProfileId:D}/{binding.Field}");
}
