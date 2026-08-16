// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.Vault;

namespace Dbr.Infrastructure.Tests.Vault;

/// <summary>
/// The field cipher, and in particular what it refuses.
/// </summary>
/// <remarks>
/// A round trip is the least interesting property here — any wrapper around AES gets
/// that right. What earns the binding its complexity is everything below it: a
/// ciphertext that will not open anywhere except the tenant, profile and field it was
/// written for, so a row copied between accounts fails loudly instead of presenting one
/// person's identity under another's name.
/// </remarks>
public class ProfileCipherTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Profile = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid OtherProfile = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("[\"Alex Whitfield\"]");

    private static ProfileFieldBinding Names => new(Tenant, Profile, ProfileField.Names);

    [Fact]
    public void A_round_trip_returns_what_went_in()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.Equal(Plaintext, ProfileCipher.Decrypt(key, Names, stored));
    }

    [Fact]
    public void The_stored_form_does_not_contain_the_plaintext()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.DoesNotContain("Whitfield", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_value_encrypts_differently_every_time()
    {
        // A deterministic ciphertext would let anyone holding the table see which
        // profiles share a name, without decrypting anything.
        using var key = NewKey();

        Assert.NotEqual(
            ProfileCipher.Encrypt(key, Names, Plaintext),
            ProfileCipher.Encrypt(key, Names, Plaintext));
    }

    [Fact]
    public void A_value_written_for_one_field_does_not_open_as_another()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileCipher.Decrypt(key, new ProfileFieldBinding(Tenant, Profile, ProfileField.Contacts), stored));
    }

    [Fact]
    public void A_value_written_for_one_profile_does_not_open_under_another()
    {
        // The case this is really for: the same tenant's own second profile. Row-level
        // security is no help there — both rows are legitimately theirs — so the only
        // thing standing between a mistaken UPDATE and a dependent's record showing
        // their parent's name is this.
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileCipher.Decrypt(key, new ProfileFieldBinding(Tenant, OtherProfile, ProfileField.Names), stored));
    }

    [Fact]
    public void A_value_written_for_one_tenant_does_not_open_under_another()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileCipher.Decrypt(key, new ProfileFieldBinding(OtherTenant, Profile, ProfileField.Names), stored));
    }

    [Fact]
    public void A_single_altered_byte_is_refused()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);
        stored[^1] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileCipher.Decrypt(key, Names, stored));
    }

    [Fact]
    public void Another_key_cannot_open_it()
    {
        using var key = NewKey();
        using var wrongKey = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            ProfileCipher.Decrypt(wrongKey, Names, stored));
    }

    [Fact]
    public void A_format_this_build_does_not_know_is_refused_rather_than_guessed_at()
    {
        using var key = NewKey();

        var stored = ProfileCipher.Encrypt(key, Names, Plaintext);
        stored[0] = 0xFF;

        var exception = Assert.Throws<CryptographicException>(() =>
            ProfileCipher.Decrypt(key, Names, stored));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_too_short_to_be_a_ciphertext_is_refused()
    {
        using var key = NewKey();

        Assert.Throws<CryptographicException>(() =>
            ProfileCipher.Decrypt(key, Names, [ProfileCipher.Version, 1, 2, 3]));
    }

    private static DataKey NewKey() => new(RandomNumberGenerator.GetBytes(32));
}
