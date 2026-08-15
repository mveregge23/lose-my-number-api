// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// A real OpenBao with its Transit engine enabled, for the tests that exercise
/// encryption.
/// </summary>
/// <remarks>
/// <para>
/// A fake key provider can be made to return plausible ciphertext, but it cannot
/// demonstrate the guarantee that matters — that a worker holding a scoped grant
/// receives only the fields it asked for, and only while the grant is live. That
/// property lives in the Transit engine, not in our code, so the tests that assert it
/// need the engine.
/// </para>
/// <para>
/// Tests that merely need some profile data to exist should use an in-process fake
/// instead. Re-proving the crypto path on every unrelated test costs container
/// start-up for no added confidence.
/// </para>
/// <para>
/// Unlike the compose stack, this runs in <c>-dev</c> mode. There, dev mode was a bug
/// worth fixing: the barrel is held in memory, so a restart destroys the keys while
/// the ciphertext they protect survives in Postgres. Here the ciphertext does not
/// outlive the container either, so there is nothing to lose and an unseal dance to
/// skip.
/// </para>
/// </remarks>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    private const string RootToken = "dbr_test_root_token";

    private readonly IContainer _container = new ContainerBuilder("openbao/openbao:2")
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
        .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
        .WithCommand("server", "-dev")
        .WithPortBinding(8200, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request
                .ForPort(8200)
                .ForPath("/v1/sys/health")))
        .Build();

    private HttpClient? _client;

    public string Address { get; private set; } = string.Empty;

    public string Token => RootToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        Address = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8200)}";
        _client = new HttpClient { BaseAddress = new Uri(Address) };
        _client.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);

        // Transit is the engine that wraps per-tenant data keys, and it is not
        // enabled by default. Doing it here rather than in each test keeps the
        // fixture equivalent to what the compose stack's init step leaves behind.
        var response = await _client.PostAsJsonAsync(
            "/v1/sys/mounts/transit",
            new { type = "transit" });

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not enable the Transit engine: {response.StatusCode} "
                + await response.Content.ReadAsStringAsync());
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        await _container.DisposeAsync();
    }

    /// <summary>Creates a named Transit key, as a tenant's data key will be.</summary>
    public async Task CreateKeyAsync(string keyName)
    {
        var response = await Client.PostAsJsonAsync($"/v1/transit/keys/{keyName}", new { });
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> EncryptAsync(string keyName, string plaintext)
    {
        var response = await Client.PostAsJsonAsync(
            $"/v1/transit/encrypt/{keyName}",
            new { plaintext = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)) });

        response.EnsureSuccessStatusCode();

        return (await ReadDataAsync(response)).GetProperty("ciphertext").GetString()!;
    }

    public async Task<string> DecryptAsync(string keyName, string ciphertext)
    {
        var response = await Client.PostAsJsonAsync(
            $"/v1/transit/decrypt/{keyName}",
            new { ciphertext });

        response.EnsureSuccessStatusCode();

        var encoded = (await ReadDataAsync(response)).GetProperty("plaintext").GetString()!;

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    /// <summary>Installs a policy, exactly as the compose stack's init step does.</summary>
    public async Task WritePolicyAsync(string name, string policy)
    {
        var response = await Client.PostAsJsonAsync($"/v1/sys/policies/acl/{name}", new { policy });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Mints a token holding one policy and nothing else — no default policy, so what
    /// it can do is exactly what that file grants.
    /// </summary>
    public async Task<string> CreateScopedTokenAsync(string policyName)
    {
        var response = await Client.PostAsJsonAsync(
            "/v1/auth/token/create",
            new { policies = new[] { policyName }, no_default_policy = true, ttl = "1h" });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("auth").GetProperty("client_token").GetString()!;
    }

    /// <summary>The raw client, for asserting on responses this wrapper doesn't model.</summary>
    public HttpClient Client =>
        _client ?? throw new InvalidOperationException("The fixture has not been initialized.");

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
}
