// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.Tests.DependencyInjection;

public class InfrastructureServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDbrPersistence_resolves_a_DbrDbContext()
    {
        var services = new ServiceCollection()
            .AddDbrPersistence(ConfigurationWith(
                ("ConnectionStrings:Core", "Host=localhost;Database=dbr;Username=dbr;Password=x")));

        // ValidateScopes/ValidateOnBuild mirror what the Development host does, so a
        // lifetime mistake in the registration surfaces here rather than at boot.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        Assert.True(context.Database.IsNpgsql());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDbrPersistence_fails_fast_without_a_core_connection_string(string? connectionString)
    {
        var configuration = connectionString is null
            ? ConfigurationWith()
            : ConfigurationWith(("ConnectionStrings:Core", connectionString));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddDbrPersistence(configuration));

        // The message is the whole value of failing here, so it is worth asserting:
        // it has to name the setting someone needs to go and fix.
        Assert.Contains("ConnectionStrings:Core", exception.Message);
    }

    private static IConfiguration ConfigurationWith(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
