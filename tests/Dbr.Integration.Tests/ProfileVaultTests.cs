// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Dbr.Domain.Identity;
using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Dbr.Infrastructure.Vault;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// A profile written and read back through the real thing: a real Postgres with both
/// roles, and a real OpenBao doing the wrapping.
/// </summary>
/// <remarks>
/// The interesting assertions are not that a name survives a round trip. They are what
/// is left in the database afterwards, what another account can see of it, and what
/// happens when the tenant's key is destroyed — none of which a fake key provider could
/// answer, because the guarantee lives in the Transit engine rather than in this code.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ProfileVaultTests(PostgresFixture postgres, OpenBaoFixture openBao)
{
    private static readonly ProfileIdentityFields Fields = new(
        ["Alex Whitfield", "A. Whitfield"],
        [new ProfileAddress(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US")],
        [new ProfileContact(Guid.NewGuid(), ProfileContactKind.Email, "alex@example.test")],
        new DateOnly(1985, 4, 17));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_profile_written_by_its_owner_reads_back_as_what_went_in()
    {
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var read = await WithProfileServiceAsync(services, tenantId, service =>
            service.ReadIdentityAsync(profile.Id, Token));

        Assert.NotNull(read);
        Assert.Equal(Fields.Names, read.Names);
        Assert.Equal(Fields.Addresses, read.Addresses);
        Assert.Equal(Fields.Contacts, read.Contacts);
        Assert.Equal(Fields.DateOfBirth, read.DateOfBirth);
    }

    [Fact]
    public async Task What_is_actually_stored_is_ciphertext()
    {
        // The claim the whole design rests on, asserted against the bytes in the table
        // rather than inferred from the fact that encryption was called.
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var stored = await ReadStoredBytesAsync(profile.Id);

        Assert.DoesNotContain("Whitfield", Encoding.UTF8.GetString(stored.Names), StringComparison.Ordinal);
        Assert.DoesNotContain("Rowan", Encoding.UTF8.GetString(stored.Addresses), StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", Encoding.UTF8.GetString(stored.Contacts), StringComparison.Ordinal);

        // And the wrapped key is not a key: it is whatever Transit handed back, which
        // is useless without Transit.
        Assert.StartsWith("vault:", stored.WrappedDataKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_account_asking_for_the_same_profile_gets_nothing()
    {
        await using var services = BuildServices();
        var owner = await NewAccountAsync(services);
        var stranger = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, owner, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        // Not an error, and not a different error from an id that never existed —
        // telling the two apart would confirm that this id belongs to somebody.
        Assert.Null(await WithProfileServiceAsync(services, stranger, service =>
            service.ReadIdentityAsync(profile.Id, Token)));

        Assert.Null(await WithProfileServiceAsync(services, stranger, service =>
            service.ReadIdentityAsync(Guid.NewGuid(), Token)));
    }

    [Fact]
    public async Task Replacing_the_fields_rewrites_them_under_a_new_key()
    {
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var before = await ReadStoredBytesAsync(profile.Id);

        var replaced = Fields with { Names = ["Alexandra Whitfield"], DateOfBirth = null };

        Assert.True(await WithProfileServiceAsync(services, tenantId, service =>
            service.ReplaceIdentityAsync(profile.Id, replaced, Token)));

        var after = await ReadStoredBytesAsync(profile.Id);

        Assert.NotEqual(before.WrappedDataKey, after.WrappedDataKey);
        Assert.NotEqual(before.Names, after.Names);
        Assert.Null(after.Dob);

        var read = await WithProfileServiceAsync(services, tenantId, service =>
            service.ReadIdentityAsync(profile.Id, Token));

        Assert.NotNull(read);
        Assert.Equal(["Alexandra Whitfield"], read.Names);
        Assert.Null(read.DateOfBirth);
    }

    [Fact]
    public async Task Replacing_the_fields_of_a_profile_that_is_not_yours_changes_nothing()
    {
        await using var services = BuildServices();
        var owner = await NewAccountAsync(services);
        var stranger = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, owner, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        Assert.False(await WithProfileServiceAsync(services, stranger, service =>
            service.ReplaceIdentityAsync(profile.Id, Fields with { Names = ["Someone Else"] }, Token)));

        var read = await WithProfileServiceAsync(services, owner, service =>
            service.ReadIdentityAsync(profile.Id, Token));

        Assert.NotNull(read);
        Assert.Equal(Fields.Names, read.Names);
    }

    [Fact]
    public async Task Destroying_the_tenants_key_makes_the_profile_unreadable()
    {
        // What "delete my account" is supposed to mean cryptographically. The row is
        // still sitting there afterwards — that is the point of asserting it this way:
        // the data is gone because nothing can unwrap it, not because a DELETE reached
        // every copy.
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        await services.GetRequiredService<IKeyManagementProvider>()
            .DestroyTenantKeyAsync(tenantId, Token);

        await Assert.ThrowsAsync<KeyManagementException>(() =>
            WithProfileServiceAsync(services, tenantId, service =>
                service.ReadIdentityAsync(profile.Id, Token)));

        Assert.NotEmpty((await ReadStoredBytesAsync(profile.Id)).Names);
    }

    [Fact]
    public async Task A_ciphertext_moved_onto_another_profile_does_not_open_there()
    {
        // The binding, end to end and under the same tenant's own two profiles — where
        // row-level security has nothing to say, because both rows are legitimately
        // theirs.
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var mine = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var dependents = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(
                ProfileRelationship.Dependent,
                "US-CA",
                "2026-06-01",
                Fields with { Names = ["Sam Whitfield"] },
                Token));

        // Every column copied, key included, so the row is internally consistent and the
        // only thing wrong with it is where it now sits. Copying one field would fail
        // for the uninteresting reason that the key no longer matched the rest.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             UPDATE vault.profile_identity AS target
             SET wrapped_data_key = source.wrapped_data_key,
                 encrypted_names = source.encrypted_names,
                 encrypted_addresses = source.encrypted_addresses,
                 encrypted_contacts = source.encrypted_contacts,
                 encrypted_dob = source.encrypted_dob
             FROM vault.profile_identity AS source
             WHERE target.privacy_profile_id = '{dependents.Id}'
               AND source.privacy_profile_id = '{mine.Id}'
             """);

        await Assert.ThrowsAsync<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
            WithProfileServiceAsync(services, tenantId, service =>
                service.ReadIdentityAsync(dependents.Id, Token)));
    }

    [Fact]
    public async Task An_account_gets_one_self_profile_and_no_more()
    {
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            WithProfileServiceAsync(services, tenantId, service =>
                service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token)));

        // The second attempt is refused by the database, not by a check somebody has to
        // remember to write on every path that creates one.
        var profiles = await WithProfileServiceAsync(services, tenantId, service =>
            service.ListAsync(Token));

        Assert.Single(profiles);
    }

    [Fact]
    public async Task The_residency_region_cannot_become_an_address()
    {
        // The one geographic field kept outside the vault. If it drifted into holding
        // something precise, it would be identifying data sitting in the store the
        // vault exists to keep identifying data out of.
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            WithProfileServiceAsync(services, tenantId, service =>
                service.CreateAsync(
                    ProfileRelationship.Self,
                    "12 Rowan Lane, Sacramento",
                    "2026-06-01",
                    Fields,
                    Token)));
    }

    [Fact]
    public async Task An_edit_that_started_from_a_stale_read_is_refused_rather_than_applied()
    {
        // The failure this exists to prevent: every change rewrites all four fields
        // under a new key, so two overlapping edits cannot merge — the second would
        // write what it read and take the first's address with it, silently.
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        using var slow = PostgresFixture.ScopeFor(services, tenantId);
        using var quick = PostgresFixture.ScopeFor(services, tenantId);

        var slowService = slow.ServiceProvider.GetRequiredService<IProfileService>();
        var quickService = quick.ServiceProvider.GetRequiredService<IProfileService>();

        // The slow editor reads the profile and holds it while somebody else finishes.
        await slowService.ReadIdentityAsync(profile.Id, Token);

        var quickResult = await quickService.AddAddressAsync(profile.Id, NewAddress("9 Elm Row"), Token);
        Assert.Equal(AddAddressOutcome.Added, quickResult.Outcome);

        await Assert.ThrowsAsync<ProfileChangedException>(() =>
            slowService.AddAddressAsync(profile.Id, NewAddress("4 Beech Way"), Token));

        // And what the quick one wrote is still there, which is the whole point of
        // refusing the other.
        var read = await WithProfileServiceAsync(services, tenantId, service =>
            service.ReadIdentityAsync(profile.Id, Token));

        Assert.NotNull(read);
        Assert.Contains(read.Addresses, address => address.Line1 == "9 Elm Row");
        Assert.DoesNotContain(read.Addresses, address => address.Line1 == "4 Beech Way");
    }

    [Fact]
    public async Task Removing_an_address_that_is_not_there_writes_nothing_at_all()
    {
        // A save would rotate the data key and rewrite every field to record that
        // somebody asked for something that was not there. Cheap to avoid, and it keeps
        // "the key changed" meaning "the data changed".
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var before = await ReadStoredBytesAsync(profile.Id);

        Assert.False(await WithProfileServiceAsync(services, tenantId, service =>
            service.RemoveAddressAsync(profile.Id, Guid.NewGuid(), Token)));

        var after = await ReadStoredBytesAsync(profile.Id);

        Assert.Equal(before.WrappedDataKey, after.WrappedDataKey);
        Assert.Equal(before.Names, after.Names);
    }

    [Fact]
    public async Task Replacing_the_details_writes_to_both_stores_and_leaves_the_addresses_alone()
    {
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        var profile = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, null, "2026-06-01", Fields, Token));

        Assert.True(await WithProfileServiceAsync(services, tenantId, service =>
            service.ReplaceDetailsAsync(
                profile.Id,
                new ProfileDetails(["Alexandra Whitfield"], null, []),
                "US-CA",
                Token)));

        var read = await WithProfileServiceAsync(services, tenantId, service =>
            service.ReadIdentityAsync(profile.Id, Token));

        Assert.NotNull(read);
        Assert.Equal(["Alexandra Whitfield"], read.Names);
        Assert.Empty(read.Contacts);
        Assert.Null(read.DateOfBirth);

        // Untouched by a call that did not mention them.
        Assert.Equal(Fields.Addresses, read.Addresses);

        // The region is the half that lands in the other store, in the clear, so that
        // resolving jurisdiction never needs the key.
        Assert.Equal(
            "US-CA",
            await postgres.QueryAsOwnerAsync<string>(
                $"SELECT residency_region FROM public.privacy_profile WHERE id = '{profile.Id}'"));
    }

    [Fact]
    public async Task A_scope_acting_for_nobody_is_refused_before_it_reaches_the_database()
    {
        await using var services = BuildServices();

        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProfileService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));
    }

    [Fact]
    public async Task The_profile_a_tenant_calls_its_own_is_the_self_one()
    {
        await using var services = BuildServices();
        var tenantId = await NewAccountAsync(services);

        await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Dependent, "US-CA", "2026-06-01", Fields, Token));

        Assert.Null(await WithProfileServiceAsync(services, tenantId, service =>
            service.FindSelfAsync(Token)));

        var self = await WithProfileServiceAsync(services, tenantId, service =>
            service.CreateAsync(ProfileRelationship.Self, "US-CA", "2026-06-01", Fields, Token));

        var found = await WithProfileServiceAsync(services, tenantId, service =>
            service.FindSelfAsync(Token));

        Assert.NotNull(found);
        Assert.Equal(self.Id, found.Id);
        Assert.Equal(ProfileRelationship.Self, found.RelationshipType);
    }

    private static ProfileAddress NewAddress(string line1) =>
        new(Guid.Empty, line1, null, "Sacramento", "CA", "95814", "US");

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("ConnectionStrings:Core", postgres.ConnectionString),
                new KeyValuePair<string, string?>("ConnectionStrings:Vault", postgres.ConnectionString),
                new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Address", openBao.Address),
                new KeyValuePair<string, string?>($"{OpenBaoOptions.SectionName}:Token", openBao.Token),
            ])
            .Build();

        // Exactly the three calls Program.cs makes, in the same order, so what these
        // tests exercise is the composition the API runs rather than a convenient
        // subset of it.
        return new ServiceCollection()
            .AddDbrPersistence(configuration)
            .AddDbrKeyManagement(configuration)
            .AddDbrVault(configuration)
            .BuildServiceProvider();
    }

    /// <summary>
    /// An account to hang profiles off, created the way signup does — acting as the
    /// tenant it is creating, since that is the only way the policy permits it.
    /// </summary>
    private static async Task<Guid> NewAccountAsync(IServiceProvider services)
    {
        var tenantId = Guid.NewGuid();

        using var scope = PostgresFixture.ScopeFor(services, tenantId);
        var core = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        core.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId,
            Email = $"{tenantId:N}@example.test",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await core.SaveChangesAsync(Token);

        return tenantId;
    }

    private static async Task<TResult> WithProfileServiceAsync<TResult>(
        IServiceProvider services,
        Guid tenantId,
        Func<IProfileService, Task<TResult>> work)
    {
        using var scope = PostgresFixture.ScopeFor(services, tenantId);

        return await work(scope.ServiceProvider.GetRequiredService<IProfileService>());
    }

    /// <summary>
    /// What is really in the row, read as the owning role so that neither the boundary
    /// nor the service can decide what this test is allowed to see.
    /// </summary>
    private async Task<StoredIdentity> ReadStoredBytesAsync(Guid profileId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            """
            SELECT wrapped_data_key, encrypted_names, encrypted_addresses, encrypted_contacts, encrypted_dob
            FROM vault.profile_identity
            WHERE privacy_profile_id = @id
            """,
            connection);

        command.Parameters.AddWithValue("id", profileId);

        await using var reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        return new StoredIdentity(
            reader.GetString(0),
            (byte[])reader[1],
            (byte[])reader[2],
            (byte[])reader[3],
            reader.IsDBNull(4) ? null : (byte[])reader[4]);
    }

    private sealed record StoredIdentity(
        string WrappedDataKey,
        byte[] Names,
        byte[] Addresses,
        byte[] Contacts,
        byte[]? Dob);
}
