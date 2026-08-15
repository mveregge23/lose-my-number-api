// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Vault;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The policy the application's key-manager token is scoped to, checked from both
/// sides: big enough to work, and no bigger.
/// </summary>
/// <remarks>
/// <para>
/// A least-privilege policy has two failure modes and they pull in opposite
/// directions. Too narrow and the application breaks — but loudly, at the first
/// operation it cannot perform. Too wide and nothing breaks at all, which is the
/// dangerous one: the policy reads as restrictive, the system behaves as though it
/// were, and the restriction is not there. Only a test that tries the things the
/// policy is supposed to forbid can tell those apart.
/// </para>
/// <para>
/// The policy under test is the file the compose stack applies, copied next to these
/// tests at build time rather than restated here. A test written against its own copy
/// would prove the copy consistent with itself and nothing about what a deployment
/// runs.
/// </para>
/// </remarks>
[Collection(OpenBaoCollection.Name)]
public class OpenBaoPolicyTests(OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string PolicyName = "dbr-api";

    private ServiceProvider _services = null!;

    private HttpClient _scoped = null!;

    public async ValueTask InitializeAsync()
    {
        var policy = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "openbao-policies", "dbr-api.hcl"));

        await openBao.WritePolicyAsync(PolicyName, policy);

        var token = await openBao.CreateScopedTokenAsync(PolicyName);

        _services = new ServiceCollection()
            .AddDbrKeyManagement(new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Address", openBao.Address),
                    new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Token", token),
                ])
                .Build())
            .BuildServiceProvider();

        // A second client carrying the same token, for asking whether things the
        // provider never attempts are refused.
        _scoped = new HttpClient { BaseAddress = new Uri(openBao.Address) };
        _scoped.DefaultRequestHeaders.Add("X-Vault-Token", token);
    }

    public async ValueTask DisposeAsync()
    {
        _scoped.Dispose();

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Everything_the_application_actually_does_is_permitted()
    {
        // The whole lifecycle on one token: create the wrapping key, mint a data key,
        // unwrap it, destroy the key. If the policy is too narrow, this is where it
        // shows — and it shows as the operation that was refused.
        var provider = _services.GetRequiredService<IKeyManagementProvider>();
        var tenantId = Guid.NewGuid();

        await provider.EnsureTenantKeyAsync(tenantId, Token);

        var generated = await provider.GenerateDataKeyAsync(tenantId, Token);
        using var key = generated.Key;

        using (var unwrapped = await provider.UnwrapDataKeyAsync(tenantId, generated.Wrapped, Token))
        {
            Assert.Equal(key.Material.ToArray(), unwrapped.Material.ToArray());
        }

        await provider.DestroyTenantKeyAsync(tenantId, Token);
    }

    [Fact]
    public async Task The_token_cannot_list_the_keys_and_therefore_cannot_enumerate_accounts()
    {
        // The most valuable thing this policy withholds. Keys are named after
        // tenants, so a token that can list them can answer "who has an account
        // here?" — the question the entire sign-in design refuses to answer. Being
        // able to decrypt and being able to enumerate are different powers, and this
        // token has only the first.
        using var request = new HttpRequestMessage(new HttpMethod("LIST"), "/v1/transit/keys");
        using var response = await _scoped.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_token_cannot_read_a_key_it_is_allowed_to_use()
    {
        // Using a key and inspecting it are separate grants. Transit never exports key
        // material, but the metadata is more than encrypting requires.
        var provider = _services.GetRequiredService<IKeyManagementProvider>();
        var tenantId = Guid.NewGuid();

        await provider.EnsureTenantKeyAsync(tenantId, Token);

        using var response = await _scoped.GetAsync(
            $"/v1/transit/keys/{OpenBaoKeyManagementProvider.KeyNameFor(tenantId)}",
            Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_token_cannot_touch_a_key_outside_the_naming_convention()
    {
        // The paths are scoped to the prefix the provider generates, which turns the
        // naming convention into something enforced rather than merely followed: code
        // that named a key anything else would be refused here rather than quietly
        // getting the run of the mount.
        using var response = await _scoped.PostAsJsonAsync(
            "/v1/transit/keys/not-a-tenant-key",
            new { type = "aes256-gcm96" },
            Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_token_cannot_encrypt_arbitrary_data_with_a_tenant_s_key()
    {
        // Nothing needs it: the only thing a wrapping key ever wraps is a data key,
        // and Transit mints those itself. A token that could also encrypt chosen
        // plaintext under a tenant's key is a token that can put content nobody
        // authored where that tenant's data lives.
        var provider = _services.GetRequiredService<IKeyManagementProvider>();
        var tenantId = Guid.NewGuid();

        await provider.EnsureTenantKeyAsync(tenantId, Token);

        using var response = await _scoped.PostAsJsonAsync(
            $"/v1/transit/encrypt/{OpenBaoKeyManagementProvider.KeyNameFor(tenantId)}",
            new { plaintext = Convert.ToBase64String("chosen"u8.ToArray()) },
            Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_token_holds_nothing_outside_the_key_manager()
    {
        // Created with no default policy, so it carries exactly what the file grants.
        // Mounting an engine is the operation that would let a compromised token give
        // itself somewhere new to work.
        using var response = await _scoped.PostAsJsonAsync(
            "/v1/sys/mounts/somewhere-else",
            new { type = "kv" },
            Token);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            $"mounting a new engine answered {response.StatusCode}");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
