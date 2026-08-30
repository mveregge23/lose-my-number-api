// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The secret a grant is presented as, and the digest it is stored as.
/// </summary>
/// <remarks>
/// One definition for both sides, because the side that mints and the side that spends run
/// in different processes and a digest computed two ways would simply never match — a
/// failure that reads as every grant being refused rather than as a mistake in a hash.
/// </remarks>
internal static class ReleaseTokens
{
    /// <summary>
    /// 256 bits, the same size as a refresh token and for the same reason: it is the whole
    /// of what stands between somebody presenting a grant and holding one.
    /// </summary>
    private const int TokenBytes = 32;

    /// <summary>A fresh secret, and the digest to store against it.</summary>
    /// <remarks>
    /// Returned together so that nothing has to remember to hash what it just generated,
    /// and so the token has no reason to exist anywhere but the one variable that hands it
    /// straight back to the caller.
    /// </remarks>
    public static (string Token, byte[] Hash) Create()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

        return (token, Digest(token));
    }

    /// <summary>
    /// SHA-256 of a presented token.
    /// </summary>
    /// <remarks>
    /// Not a slow hash, deliberately. The input is 256 bits from a CSPRNG rather than
    /// something a person chose, so there is nothing to guess and no dictionary to slow
    /// down — and this is on the path a scan takes once per company.
    /// </remarks>
    public static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
