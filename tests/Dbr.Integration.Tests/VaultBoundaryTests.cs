// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Vault;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// The wall between the two stores, checked from both sides.
/// </summary>
/// <remarks>
/// Two roles, and what each of them cannot do is the whole of the design. The role
/// serving ordinary traffic cannot reach identifying data at all; the role that can
/// reach it cannot see anything to join it against. Written as SQL run under each role
/// deliberately — the property belongs to the database, and asserting it through the
/// application would only prove that the application does not currently try.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class VaultBoundaryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_application_role_cannot_read_identifying_data()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            RunAsAsync(TenantSessionInterceptor.ApplicationRole, "SELECT count(*) FROM vault.profile_identity"));

        // 42501: insufficient privilege. It never reaches the policy, because it cannot
        // reach the schema.
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_application_role_cannot_write_identifying_data_either()
    {
        // Acting as a tenant, and writing that same tenant's row — so the policy has no
        // objection and the refusal can only be the missing grant. Without the tenant
        // set this would fail anyway, on the policy, and would pass whether the role
        // split existed or not.
        var tenantId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            RunAsAsync(
                TenantSessionInterceptor.ApplicationRole,
                $"""
                 INSERT INTO vault.profile_identity
                     (privacy_profile_id, tenant_id, wrapped_data_key,
                      encrypted_names, encrypted_addresses, encrypted_contacts)
                 VALUES (gen_random_uuid(), '{tenantId}', 'x', '\x00', '\x00', '\x00')
                 """,
                tenantId));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_vault_role_cannot_read_the_accounts_its_rows_belong_to()
    {
        // The half that makes "never joined into general query paths" structural. A
        // query issued over the vault connection cannot bring a name alongside an email
        // address, however it is written, because the account table is not reachable
        // from this role at all.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            RunAsAsync(VaultSessionInterceptor.VaultRole, "SELECT count(*) FROM public.tenant"));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_vault_role_cannot_read_the_profile_rows_on_the_other_side()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            RunAsAsync(VaultSessionInterceptor.VaultRole, "SELECT count(*) FROM public.privacy_profile"));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Neither_role_can_bypass_row_level_security()
    {
        // Without this the isolation tests either side would keep passing while
        // isolating nothing: a superuser or a BYPASSRLS role skips every policy.
        foreach (var role in new[] { TenantSessionInterceptor.ApplicationRole, VaultSessionInterceptor.VaultRole })
        {
            Assert.False(
                await postgres.QueryAsOwnerAsync<bool>(
                    $"""
                     SELECT (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole)
                     FROM pg_roles WHERE rolname = '{role}'
                     """),
                $"{role} can bypass the policies it is supposed to be subject to.");
        }
    }

    [Fact]
    public async Task Identifying_rows_are_invisible_to_every_other_tenant()
    {
        // Through the real context and the real interceptor, so what is exercised is the
        // path the profile service uses rather than a hand-written connection.
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        await using var services = postgres.BuildServices();

        using (var scope = PostgresFixture.ScopeFor(services, owner))
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

            vault.Set<ProfileIdentity>().Add(new ProfileIdentity
            {
                PrivacyProfileId = profileId,
                TenantId = owner,
                WrappedDataKey = "vault:v1:not-a-real-key",
                EncryptedNames = [1, 2, 3],
                EncryptedAddresses = [4, 5, 6],
                EncryptedContacts = [7, 8, 9],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await vault.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = PostgresFixture.ScopeFor(services, stranger))
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

            Assert.Null(await vault.Set<ProfileIdentity>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    row => row.PrivacyProfileId == profileId,
                    TestContext.Current.CancellationToken));
        }

        // IgnoreQueryFilters above is the point of the assertion: with the application's
        // own filter switched off, what is left holding the line is the database policy.
        // The row is still there — the owner can still see it.
        using (var scope = PostgresFixture.ScopeFor(services, owner))
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

            Assert.NotNull(await vault.Set<ProfileIdentity>()
                .FirstOrDefaultAsync(
                    row => row.PrivacyProfileId == profileId,
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_connection_with_no_tenant_sees_no_identifying_rows_at_all()
    {
        var owner = Guid.NewGuid();

        await using var services = postgres.BuildServices();

        using (var scope = PostgresFixture.ScopeFor(services, owner))
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

            vault.Set<ProfileIdentity>().Add(new ProfileIdentity
            {
                PrivacyProfileId = Guid.NewGuid(),
                TenantId = owner,
                WrappedDataKey = "vault:v1:not-a-real-key",
                EncryptedNames = [1],
                EncryptedAddresses = [2],
                EncryptedContacts = [3],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await vault.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = PostgresFixture.ScopeFor(services, null))
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

            Assert.Empty(await vault.Set<ProfileIdentity>()
                .IgnoreQueryFilters()
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// Runs one statement having assumed <paramref name="role"/>, the way the
    /// interceptors do.
    /// </summary>
    private async Task RunAsAsync(string role, string sql, Guid? tenantId = null)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            $"SET ROLE {role}; SELECT set_config('{RoleSessionInterceptor.TenantSetting}', @tenant, false); {sql};",
            connection);

        command.Parameters.AddWithValue("tenant", tenantId?.ToString() ?? string.Empty);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
