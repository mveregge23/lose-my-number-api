// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;

namespace Dbr.Domain.Vault;

/// <summary>
/// A data-encryption key in the clear, held for as short a time as possible.
/// </summary>
/// <remarks>
/// <para>
/// Disposable because key material should not outlive its use. Disposing overwrites
/// the bytes, which narrows the window in which a memory dump, a swap file or a crash
/// report contains a key that decrypts somebody's identity.
/// </para>
/// <para>
/// <b>It narrows the window; it does not close it.</b> A garbage collector is free to
/// copy an array as it compacts the heap, and nothing can overwrite a copy it no
/// longer has a reference to. Treating this as a guarantee would be worse than not
/// having it, because it would justify holding keys longer than necessary. Hold them
/// briefly, and let this reduce what is left behind.
/// </para>
/// </remarks>
public sealed class DataKey : IDisposable
{
    private readonly byte[] _material;

    private bool _disposed;

    public DataKey(byte[] material)
    {
        ArgumentNullException.ThrowIfNull(material);

        _material = material;
    }

    /// <summary>
    /// The key itself, as a span rather than the array, so that reading it does not
    /// hand out a reference that can outlive this object.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The key has already been wiped.</exception>
    public ReadOnlySpan<byte> Material
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _material;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_material);
        _disposed = true;
    }
}
