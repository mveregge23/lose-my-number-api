// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using Dbr.Domain.Vault;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The envelope every encrypted field in this system is written in.
/// </summary>
/// <remarks>
/// <para>
/// Extracted when a second kind of value needed encrypting, and shared rather than copied for
/// the obvious reason: two implementations of an authenticated cipher are two chances to get
/// a nonce or a tag length wrong, and the second one gets less attention than the first.
/// </para>
/// <para>
/// <b>What differs between callers is the associated data, and that is the whole point.</b>
/// It is authenticated but not stored, so a ciphertext only decrypts in the exact position it
/// was written to — copying one row's bytes over another's produces a decryption failure
/// rather than one person's data appearing under someone else's account. Each caller composes
/// its own binding string naming what "position" means for it.
/// </para>
/// <para>
/// AES-256-GCM with a fresh random nonce per encryption, so writing the same value twice does
/// not produce the same bytes twice. The stored form is a version byte, the nonce, then the
/// ciphertext and its tag; the version is there because the alternative to being able to
/// change algorithms is re-encrypting everything under a format nothing can identify.
/// </para>
/// </remarks>
internal static class FieldCipher
{
    /// <summary>The only format written today.</summary>
    public const byte Version = 1;

    private const int NonceSize = 12;

    private const int TagSize = 16;

    public static byte[] Encrypt(
        DataKey key,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);

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
            associatedData);

        return output;
    }

    /// <exception cref="CryptographicException">
    /// The ciphertext was altered, was encrypted under a different key, or was written for a
    /// different position than the one it is being read in.
    /// </exception>
    public static byte[] Decrypt(
        DataKey key,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(key);

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
            associatedData);

        return plaintext;
    }
}
