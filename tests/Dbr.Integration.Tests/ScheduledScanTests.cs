// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Dbr.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// Planning the recurring scans: who the scheduler can see, what it may do to them, and
/// what happens when it runs twice.
/// </summary>
/// <remarks>
/// The privileged half of this story is one role reading one column, and its limits are
/// properties of the database rather than of the code — so they are asserted by asking
/// the database to do the things the role should not be able to do.
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class ScheduledScanTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>(
                    $"ConnectionStrings:{InfrastructureServiceCollectionExtensions.CoreConnectionStringName}",
                    postgres.ConnectionString),
                new KeyValuePair<string, string?>(
                    "Consent:PolicyVersion",
                    DbrApiFactory.ConsentPolicyVersion),
            ])
            .Build();

        // The worker's registrations rather than the API's — the API deliberately does not
        // get the account directory, and a test provider that handed it one would be
        // testing a composition nobody ships.
        _services = new ServiceCollection()
            .AddDbrPersistence(configuration)
            .AddDbrConsent(configuration)
            .AddDbrScanScheduling(configuration)
            .BuildServiceProvider();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _services.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await postgres.ExecuteAsOwnerAsync(
            """
            DELETE FROM vault.exposure_source;
             DELETE FROM public.exposure;
            DELETE FROM public.scan_broker;
            DELETE FROM public.scan;
            DELETE FROM public.consent_record;
            DELETE FROM vault.profile_identity;
            DELETE FROM public.privacy_profile;
            DELETE FROM public.tenant;
            DELETE FROM public.passkey_ceremony;
            """);
    }

    [Fact]
    public async Task The_directory_sees_every_account()
    {
        // The one question no tenant-scoped role can answer, and the reason the third role
        // exists at all.
        var (_, first) = await OpenAccountAsync();
        var (_, second) = await OpenAccountAsync();

        var ids = await _services.GetRequiredService<IAccountDirectory>()
            .ListAccountIdsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
    }

    [Fact]
    public async Task The_scheduler_role_cannot_read_an_email_address()
    {
        // The grant is column-level, so this is refused rather than merely not asked for.
        // Enumerating accounts is a privilege; reading who they are is a different one,
        // and the role that needs the first must not quietly acquire the second.
        await OpenAccountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                """
                SET ROLE dbr_scheduler;
                SELECT email FROM public.tenant LIMIT 1;
                """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_scheduler_role_cannot_write_anything()
    {
        var (_, tenantId) = await OpenAccountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_scheduler;
                 UPDATE public.tenant SET status = 'suspended' WHERE id = '{tenantId}';
                 """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Theory]
    [InlineData("privacy_profile")]
    [InlineData("exposure")]
    [InlineData("consent_record")]
    [InlineData("scan_leg")]
    [InlineData("identity_release")]
    public async Task The_scheduler_role_cannot_reach_any_other_table(string table)
    {
        // Refused by the grant, before row-level security is consulted at all — the role
        // holds no privilege on these tables, so there is nothing for a policy to filter.
        // Two independent layers therefore say no, and this is the outer one.
        //
        // What the policy choice buys is that the relaxation stayed put: the exemptions
        // written for this role name the two tables they were written for, so a table added
        // tomorrow is not silently included. BYPASSRLS would have been an attribute of the
        // role instead, exempting it from every policy everywhere in order to relax two.
        //
        // The scan table is not in this list any more, and that is the point of the list:
        // it was added deliberately, in a migration that says why, and the tests covering
        // exactly how far that reaches live with the dispatcher that needed it.
        await OpenAccountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_scheduler;
                 SELECT count(*) FROM public.{table};
                 """));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task A_run_queues_one_scheduled_scan_for_each_identity()
    {
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var run = await RunFor(tenantId);

        Assert.False(run.ConsentMissing);
        Assert.Equal(1, run.Queued);

        var trigger = await postgres.QueryAsOwnerAsync<string>(
            $"SELECT trigger FROM public.scan WHERE tenant_id = '{tenantId}'");

        Assert.Equal("scheduled", trigger);

        // Not narrowed: a recurring scan is the whole catalog by definition, and a subset
        // chosen once would silently become the subset monitored forever.
        var narrowed = await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan_broker WHERE tenant_id = '{tenantId}'");

        Assert.Equal(0L, narrowed);
    }

    [Fact]
    public async Task An_account_that_has_not_permitted_scanning_is_skipped()
    {
        // The case the consent check exists for. Somebody who withdrew permission is not
        // searched for this month, and nothing has to remember to stop the schedule.
        var (_, tenantId) = await OpenAccountAsync();

        var run = await RunFor(tenantId);

        Assert.True(run.ConsentMissing);
        Assert.Equal(0, run.Queued);
        Assert.Equal(0L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task Withdrawing_permission_stops_next_months_scan()
    {
        var (token, tenantId) = await OpenAccountAsync();

        await GrantScanAsync(token);
        Assert.Equal(1, (await RunFor(tenantId)).Queued);

        await SetScanConsentAsync(token, false);

        // Clear the day's row so this is testing consent rather than the idempotency guard.
        await postgres.ExecuteAsOwnerAsync($"DELETE FROM public.scan WHERE tenant_id = '{tenantId}';");

        Assert.True((await RunFor(tenantId)).ConsentMissing);
        Assert.Equal(0L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task Running_twice_in_a_day_queues_nothing_the_second_time()
    {
        // A restarted scheduler, a replayed misfire, an operator starting a second worker.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var first = await RunFor(tenantId);
        var second = await RunFor(tenantId);

        Assert.Equal(1, first.Queued);
        Assert.Equal(0, second.Queued);
        Assert.Equal(1, second.AlreadyQueued);
        Assert.Equal(1L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task The_database_refuses_a_second_scheduled_scan_on_the_same_day()
    {
        // The check in the runner handles the ordinary case and does nothing for two
        // schedulers checking at the same moment. This is the guarantee.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);
        await RunFor(tenantId);

        var profileId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT privacy_profile_id FROM public.scan WHERE tenant_id = '{tenantId}'");

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 SET ROLE dbr_app;
                 SELECT set_config('app.tenant_id', '{tenantId}', false);
                 INSERT INTO public.scan (tenant_id, privacy_profile_id, trigger, status)
                     VALUES ('{tenantId}', '{profileId}', 'scheduled', 'queued');
                 """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task Asking_for_two_scans_by_hand_on_one_day_is_still_allowed()
    {
        // The index is scoped to scheduled runs on purpose. Wanting to look twice in a day
        // is a person's prerogative, and this is not a rate limit.
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var first = await _api.PostAsync("/api/v1/scans", new { }, token);
        var second = await _api.PostAsync("/api/v1/scans", new { }, token);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, first.Status);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, second.Status);
        Assert.Equal(2L, await ScanCountAsync(tenantId));
    }

    [Fact]
    public async Task A_run_for_one_account_writes_nothing_for_another()
    {
        var (mineToken, mine) = await OpenAccountAsync();
        var (theirsToken, theirs) = await OpenAccountAsync();

        await GrantScanAsync(mineToken);
        await GrantScanAsync(theirsToken);

        await RunFor(mine);

        Assert.Equal(1L, await ScanCountAsync(mine));
        Assert.Equal(0L, await ScanCountAsync(theirs));
    }

    [Fact]
    public async Task The_job_plans_for_the_accounts_due_today_and_no_others()
    {
        // The glue, end to end: read every account, keep the ones whose day this is, and
        // give each of them its own scope. Driven against a real database with a clock
        // this test controls, because the filtering is the whole job and a run on the
        // wrong day is indistinguishable from a run that did nothing.
        // Several accounts rather than two, and the assertion is over whichever of them
        // share the chosen day. Ids are random, so any two can land on the same day — a
        // test written around exactly one being due would skip or fail on a coin toss
        // rather than on anything about the code.
        var accounts = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var (token, id) = await OpenAccountAsync();
            await GrantScanAsync(token);
            accounts.Add(id);
        }

        var chosenDay = ScanSchedule.DayOfMonthFor(accounts[0]);
        var due = accounts.Where(id => ScanSchedule.DayOfMonthFor(id) == chosenDay).ToList();
        var notDue = accounts.Except(due).ToList();

        await JobWithClockAt(new DateTime(2026, 9, chosenDay, 2, 0, 0, DateTimeKind.Utc))
            .PlanAsync(TestContext.Current.CancellationToken);

        foreach (var id in due)
        {
            Assert.Equal(1L, await ScanCountAsync(id));
        }

        foreach (var id in notDue)
        {
            Assert.Equal(0L, await ScanCountAsync(id));
        }

        // And the split was a real one rather than everybody landing together, which would
        // have made the second loop vacuous.
        Assert.NotEmpty(notDue);
    }

    [Fact]
    public async Task The_job_plans_nothing_on_a_day_nobody_is_due()
    {
        var (token, tenantId) = await OpenAccountAsync();
        await GrantScanAsync(token);

        var day = ScanSchedule.DayOfMonthFor(tenantId);
        var notTheirDay = day == 28 ? 1 : day + 1;

        await JobWithClockAt(new DateTime(2026, 9, notTheirDay, 2, 0, 0, DateTimeKind.Utc))
            .PlanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScanCountAsync(tenantId));
    }

    private ScheduledScanJob JobWithClockAt(DateTime utc) =>
        new(
            _services.GetRequiredService<IAccountDirectory>(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(new DateTimeOffset(utc)),
            NullLogger<ScheduledScanJob>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<ScheduledScanRun> RunFor(Guid tenantId)
    {
        // A scope per account, exactly as the job does it. The tenant is write-once, so
        // reusing one would mean a unit of work spanning every account on the instance.
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        return await scope.ServiceProvider
            .GetRequiredService<IScheduledScanRunner>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> ScanCountAsync(Guid tenantId) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.scan WHERE tenant_id = '{tenantId}'");

    private async Task GrantScanAsync(string token) => await SetScanConsentAsync(token, true);

    private async Task SetScanConsentAsync(string token, bool granted) =>
        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

    private async Task<(string AccessToken, Guid TenantId)> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"sched-{Guid.NewGuid():N}@example.test", authenticator);

        return (ApiClient.AccessToken(session), ApiClient.TenantId(session));
    }
}
