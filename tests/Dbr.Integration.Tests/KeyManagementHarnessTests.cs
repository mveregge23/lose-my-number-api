// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net.Http.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Proves the OpenBao fixture works, ahead of the encryption code that will use it.
/// </summary>
/// <remarks>
/// A harness nobody has watched succeed is a harness that will be debugged later,
/// under time pressure, alongside the feature it was supposed to be supporting. These
/// exercise the operations the vault service will actually perform — create a key,
/// encrypt, decrypt — so that when that code arrives, a failure points at the code
/// rather than at the fixture.
/// </remarks>
[Collection(OpenBaoCollection.Name)]
public class KeyManagementHarnessTests(OpenBaoFixture openBao)
{
    [Fact]
    public async Task Transit_round_trips_a_value_through_a_named_key()
    {
        var keyName = $"tenant-{Guid.NewGuid():N}";
        const string Plaintext = "1 Privacy Lane, Springfield";

        await openBao.CreateKeyAsync(keyName);

        var ciphertext = await openBao.EncryptAsync(keyName, Plaintext);

        Assert.StartsWith("vault:v1:", ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("Privacy Lane", ciphertext, StringComparison.Ordinal);
        Assert.Equal(Plaintext, await openBao.DecryptAsync(keyName, ciphertext));
    }

    [Fact]
    public async Task A_key_cannot_decrypt_what_another_key_encrypted()
    {
        // The property that makes per-tenant keys worth having: destroying one
        // tenant's key has to render that tenant's ciphertext unrecoverable without
        // touching anyone else's. That only holds if the keys are genuinely separate,
        // which is a claim about the engine rather than about our code.
        var alice = $"tenant-{Guid.NewGuid():N}";
        var bob = $"tenant-{Guid.NewGuid():N}";

        await openBao.CreateKeyAsync(alice);
        await openBao.CreateKeyAsync(bob);

        var ciphertext = await openBao.EncryptAsync(alice, "alice's address");

        var response = await openBao.Client.PostAsJsonAsync(
            $"/v1/transit/decrypt/{bob}",
            new { ciphertext },
            TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Encrypting_the_same_value_twice_does_not_produce_the_same_ciphertext()
    {
        // Otherwise ciphertext becomes a matching key: equal encrypted values would
        // reveal that two profiles share an address, without decrypting anything.
        var keyName = $"tenant-{Guid.NewGuid():N}";
        await openBao.CreateKeyAsync(keyName);

        var first = await openBao.EncryptAsync(keyName, "same value");
        var second = await openBao.EncryptAsync(keyName, "same value");

        Assert.NotEqual(first, second);
    }
}
