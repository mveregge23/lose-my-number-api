// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;

namespace Dbr.Infrastructure.Tests.Vault;

/// <summary>
/// The holder for key material in the clear.
/// </summary>
public class DataKeyTests
{
    [Fact]
    public void It_hands_back_the_key_it_was_given()
    {
        using var key = new DataKey([1, 2, 3, 4]);

        Assert.Equal<byte[]>([1, 2, 3, 4], key.Material.ToArray());
    }

    [Fact]
    public void Disposing_overwrites_the_key()
    {
        // Asserted against the caller's own array, because that is the memory that
        // would otherwise still hold a usable key after the object was finished with.
        var material = new byte[] { 9, 9, 9, 9 };

        new DataKey(material).Dispose();

        Assert.Equal<byte[]>([0, 0, 0, 0], material);
    }

    [Fact]
    public void Reading_a_wiped_key_is_an_error_rather_than_a_row_of_zeroes()
    {
        // Zeroes are a valid-looking key. Handing them back would mean encrypting
        // something with a key of nothing, which fails somewhere far from here.
        var key = new DataKey([1, 2, 3, 4]);
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Material.ToArray());
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var key = new DataKey([1, 2, 3, 4]);

        key.Dispose();
        key.Dispose();
    }
}
