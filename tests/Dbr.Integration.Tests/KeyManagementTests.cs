// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Vault;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The adapter that stands between everything which encrypts and the service holding
/// the keys.
/// </summary>
/// <remarks>
/// Against a real Transit engine, because the guarantees being asserted are its
/// guarantees. A fake provider can return convincing ciphertext and a plausible
/// wrapped key; what it cannot do is fail to unwrap after the wrapping key is
/// destroyed, which is the property account deletion is going to rest on.
/// </remarks>
[Collection(OpenBaoCollection.Name)]
public class KeyManagementTests(OpenBaoFixture openBao) : IDisposable
{
    private readonly ServiceProvider _services = BuildServices(openBao);

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_data_key_survives_a_round_trip_through_its_wrapped_form()
    {
        var tenantId = await NewTenantAsync();

        var generated = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var original = generated.Key;

        using var unwrapped = await Provider.UnwrapDataKeyAsync(tenantId, generated.Wrapped, Token);

        Assert.Equal(original.Material.ToArray(), unwrapped.Material.ToArray());
    }

    [Fact]
    public async Task A_data_key_is_the_length_the_ciphers_downstream_will_expect()
    {
        // 256 bits. Asserted because the number is decided by the key manager rather
        // than by this code, so a change of engine or of key type could quietly hand
        // back something the encryption above it cannot use.
        var tenantId = await NewTenantAsync();

        var generated = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var key = generated.Key;

        Assert.Equal(32, key.Material.Length);
    }

    [Fact]
    public async Task The_wrapped_form_does_not_contain_the_key()
    {
        // The whole point of storing the wrapped form. If the plaintext were
        // recoverable from it by looking, the database would be holding keys.
        var tenantId = await NewTenantAsync();

        var generated = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var key = generated.Key;

        Assert.StartsWith("vault:v1:", generated.Wrapped, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(key.Material),
            generated.Wrapped,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_generation_produces_a_different_key()
    {
        var tenantId = await NewTenantAsync();

        var first = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var firstKey = first.Key;

        var second = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var secondKey = second.Key;

        Assert.NotEqual(firstKey.Material.ToArray(), secondKey.Material.ToArray());
    }

    [Fact]
    public async Task One_tenant_cannot_unwrap_another_tenant_s_key()
    {
        // What per-tenant wrapping keys are for. Without this, "destroying one
        // tenant's key" would not mean anything, because any other key would still
        // open the same ciphertext.
        var alice = await NewTenantAsync();
        var bob = await NewTenantAsync();

        var generated = await Provider.GenerateDataKeyAsync(alice, Token);
        using var key = generated.Key;

        await Assert.ThrowsAsync<KeyManagementException>(
            () => Provider.UnwrapDataKeyAsync(bob, generated.Wrapped, Token));
    }

    [Fact]
    public async Task Destroying_the_wrapping_key_makes_everything_it_wrapped_unreadable()
    {
        // The cryptographic half of deleting an account, and the reason erasure does
        // not depend on finding every copy of a row: after this, the wrapped key is
        // just bytes, wherever it happens to be sitting.
        var tenantId = await NewTenantAsync();

        var generated = await Provider.GenerateDataKeyAsync(tenantId, Token);
        using var key = generated.Key;

        // It reads perfectly well right up until the key is destroyed.
        (await Provider.UnwrapDataKeyAsync(tenantId, generated.Wrapped, Token)).Dispose();

        await Provider.DestroyTenantKeyAsync(tenantId, Token);

        await Assert.ThrowsAsync<KeyManagementException>(
            () => Provider.UnwrapDataKeyAsync(tenantId, generated.Wrapped, Token));
    }

    [Fact]
    public async Task Destroying_one_tenant_s_key_leaves_everyone_else_readable()
    {
        var doomed = await NewTenantAsync();
        var bystander = await NewTenantAsync();

        var theirs = await Provider.GenerateDataKeyAsync(bystander, Token);
        using var theirKey = theirs.Key;

        await Provider.DestroyTenantKeyAsync(doomed, Token);

        using var stillWorks = await Provider.UnwrapDataKeyAsync(bystander, theirs.Wrapped, Token);

        Assert.Equal(theirKey.Material.ToArray(), stillWorks.Material.ToArray());
    }

    [Fact]
    public async Task Making_sure_a_key_exists_can_be_done_twice()
    {
        // It runs on a path that may be retried, so it has to be safe to repeat rather
        // than something the caller must first check.
        var tenantId = Guid.NewGuid();

        await Provider.EnsureTenantKeyAsync(tenantId, Token);
        await Provider.EnsureTenantKeyAsync(tenantId, Token);

        var generated = await Provider.GenerateDataKeyAsync(tenantId, Token);
        generated.Key.Dispose();

        Assert.NotEmpty(generated.Wrapped);
    }

    [Fact]
    public async Task A_tenant_with_no_key_cannot_generate_one_by_accident()
    {
        // Generating against a key that was never created has to fail rather than
        // quietly making one: a key created implicitly is a key nothing recorded the
        // existence of, and erasure works by name.
        await Assert.ThrowsAsync<KeyManagementException>(
            () => Provider.GenerateDataKeyAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task A_wrapped_value_that_is_not_one_is_refused()
    {
        var tenantId = await NewTenantAsync();

        await Assert.ThrowsAsync<KeyManagementException>(
            () => Provider.UnwrapDataKeyAsync(tenantId, "not-a-wrapped-key", Token));
    }

    [Fact]
    public async Task A_key_manager_that_refuses_is_not_mistaken_for_one_that_agreed()
    {
        // The operations with nothing to read back are the ones that can fail
        // silently: creating a key and destroying one both answer with no body, so
        // there is no missing field to trip over and only the status says what
        // happened. A destroy that quietly did nothing is the worst version of this —
        // erasure would report success while the key it was supposed to obliterate
        // sat there intact.
        using var misconfigured = BuildServices(openBao, transitMount: "no-engine-here");
        var provider = misconfigured.GetRequiredService<IKeyManagementProvider>();

        await Assert.ThrowsAsync<KeyManagementException>(
            () => provider.EnsureTenantKeyAsync(Guid.NewGuid(), Token));

        await Assert.ThrowsAsync<KeyManagementException>(
            () => provider.DestroyTenantKeyAsync(Guid.NewGuid(), Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IKeyManagementProvider Provider => _services.GetRequiredService<IKeyManagementProvider>();

    private static ServiceProvider BuildServices(OpenBaoFixture openBao, string transitMount = "transit") =>
        new ServiceCollection()
            .AddDbrKeyManagement(new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Address", openBao.Address),
                    new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Token", openBao.Token),
                    new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:TransitMount", transitMount),
                ])
                .Build())
            .BuildServiceProvider();

    private async Task<Guid> NewTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await Provider.EnsureTenantKeyAsync(tenantId, Token);

        return tenantId;
    }
}
