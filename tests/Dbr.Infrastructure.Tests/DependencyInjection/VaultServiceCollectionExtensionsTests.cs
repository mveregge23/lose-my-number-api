// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Dbr.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.Tests.DependencyInjection;

public class VaultServiceCollectionExtensionsTests
{
    private const string LocalPostgres = "Host=localhost;Database=dbr;Username=dbr;Password=x";

    [Fact]
    public void The_composition_root_resolves_the_vault_store_and_the_service_over_it()
    {
        // Registered together, the way Program.cs does, because a profile spans both
        // stores and is unreadable without the key manager — validating the vault
        // registration on its own would prove less than it appears to.
        var configuration = ConfigurationWith(
            ("ConnectionStrings:Core", LocalPostgres),
            ("ConnectionStrings:Vault", LocalPostgres),
            ("Bao:Address", "http://127.0.0.1:1"),
            ("Bao:Token", "not-a-usable-token"));

        var services = new ServiceCollection()
            .AddDbrPersistence(configuration)
            .AddDbrKeyManagement(configuration)
            .AddDbrVault(configuration);

        // ValidateScopes/ValidateOnBuild mirror what the Development host does, so a
        // lifetime mistake in the registration surfaces here rather than at boot.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<VaultDbContext>().Database.IsNpgsql());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProfileService>());
    }

    [Fact]
    public void Both_stores_act_for_the_same_tenant()
    {
        // Two contexts, one tenant object. A second copy would let a request establish
        // its account against one store and reach the other as nobody — where the
        // policies would return nothing and the failure would look like missing data
        // rather than a wiring mistake.
        var configuration = ConfigurationWith(
            ("ConnectionStrings:Core", LocalPostgres),
            ("ConnectionStrings:Vault", LocalPostgres));

        using var provider = new ServiceCollection()
            .AddDbrPersistence(configuration)
            .AddDbrVault(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(Guid.NewGuid());

        Assert.Equal(
            scope.ServiceProvider.GetRequiredService<DbrDbContext>().CurrentTenantId,
            scope.ServiceProvider.GetRequiredService<VaultDbContext>().CurrentTenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDbrVault_fails_fast_without_a_vault_connection_string(string? connectionString)
    {
        var configuration = connectionString is null
            ? ConfigurationWith()
            : ConfigurationWith(("ConnectionStrings:Vault", connectionString));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDbrVault(configuration));

        Assert.Contains("ConnectionStrings:Vault", exception.Message);
    }

    [Fact]
    public void The_core_connection_string_is_not_accepted_in_its_place()
    {
        // Falling back to the core store would be the worst kind of convenience: the
        // application would work, the tests would pass, and identity fields would be
        // reached over a connection that had never assumed the vault role — meaning a
        // deployment that had moved the vault elsewhere would be writing to the wrong
        // database entirely.
        var configuration = ConfigurationWith(
            ("ConnectionStrings:Core", "Host=localhost;Database=dbr;Username=dbr;Password=x"));

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDbrVault(configuration));
    }

    [Fact]
    public void The_profile_service_is_what_callers_get()
    {
        // Registered against the interface rather than the class, because the whole
        // arrangement depends on nothing outside this assembly holding a type that can
        // reach the vault context directly.
        var services = new ServiceCollection()
            .AddDbrVault(ConfigurationWith(
                ("ConnectionStrings:Vault", "Host=localhost;Database=dbr;Username=dbr;Password=x")));

        Assert.Contains(services, service => service.ServiceType == typeof(IProfileService));
    }

    private static IConfiguration ConfigurationWith(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
