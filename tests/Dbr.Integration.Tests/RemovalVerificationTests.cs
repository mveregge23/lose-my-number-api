// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Removals;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dbr.Integration.Tests;

/// <summary>
/// The three transitions the lifecycle drew and nothing drove.
/// </summary>
/// <remarks>
/// <para>
/// Before this, a demand that reached <c>AwaitingBrokerResponse</c> stayed there for ever:
/// nothing confirmed it, nothing expired it, and nothing noticed a listing coming back. The
/// claims here are that a finished run settles it, that it settles it in the right direction,
/// and — the one worth the most — that a run which could not see straight settles nothing.
/// </para>
/// <para>
/// Rows are built directly rather than driven through the API, because what is under test is
/// what a finished run concludes, and the route that produces the run has its own tests. The
/// database is real, so the lifecycle constraint and the tenant boundary are the ones that
/// actually ship.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class RemovalVerificationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _profileId;
    private Guid _brokerId;

    private DateTimeOffset Now => _clock.GetUtcNow();

    public async ValueTask InitializeAsync()
    {
        _tenantId = Guid.NewGuid();
        _profileId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.tenant (id, email, status)
                 VALUES ('{_tenantId}', 'verify-{_suffix}@example.test', 'active');

             INSERT INTO public.privacy_profile
                 (id, tenant_id, relationship_type, attestation_version)
                 VALUES ('{_profileId}', '{_tenantId}', 'self', '2026-06-01');

             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Verify Broker {_suffix}', 'verify-{_suffix}.test', 'email', 45, true);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = 'verify-{_suffix}.test'");
    }

    public async ValueTask DisposeAsync() =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.removal_request WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.exposure WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.scan_leg WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.scan_broker WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.scan WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.privacy_profile WHERE tenant_id = '{_tenantId}';
             DELETE FROM public.tenant WHERE id = '{_tenantId}';
             DELETE FROM public.broker WHERE domain = 'verify-{_suffix}.test';
             """);

    [Fact]
    public async Task A_run_that_finds_nothing_confirms_the_demand()
    {
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(30));
        var scanId = await RunAsync(ScanLegOutcome.NothingFound, recorded: 0);

        Assert.Equal(1, await ResolveAsync(scanId));

        Assert.Equal("removed", await StatusOfAsync(requestId));
    }

    [Fact]
    public async Task Good_news_does_not_wait_for_the_deadline()
    {
        // A listing that is gone is gone. Holding the confirmation back until the company's
        // time expired would be withholding the answer somebody asked for.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(44));
        var scanId = await RunAsync(ScanLegOutcome.NothingFound, recorded: 0);

        await ResolveAsync(scanId);

        Assert.Equal("removed", await StatusOfAsync(requestId));
    }

    [Fact]
    public async Task Candidates_that_were_all_below_the_floor_are_nothing_found()
    {
        // The floor already decided these were not this person. A run that saw faces and
        // recognised none of them found nobody.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(30));
        var scanId = await RunAsync(ScanLegOutcome.Found, recorded: 0);

        await ResolveAsync(scanId);

        Assert.Equal("removed", await StatusOfAsync(requestId));
    }

    [Fact]
    public async Task Still_listed_before_the_deadline_is_not_yet_an_answer()
    {
        // The company is still inside the time the statute gives it, and recording a failure
        // here would hold them to a deadline that has not arrived.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(30));
        var scanId = await RunAsync(ScanLegOutcome.Found, recorded: 1);

        Assert.Equal(0, await ResolveAsync(scanId));

        Assert.Equal("awaiting_broker_response", await StatusOfAsync(requestId));
    }

    [Fact]
    public async Task Still_listed_after_the_deadline_is_a_failure_somebody_looked_at()
    {
        // The point of the story. "Failed" now means we went and checked rather than that a
        // timer expired, which is the difference between a status somebody can act on and a
        // record of our own impatience.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(-1));
        var scanId = await RunAsync(ScanLegOutcome.Found, recorded: 1);

        Assert.Equal(1, await ResolveAsync(scanId));

        Assert.Equal("failed", await StatusOfAsync(requestId));
    }

    [Theory]
    [InlineData(ScanLegOutcome.RateLimited)]
    [InlineData(ScanLegOutcome.Blocked)]
    [InlineData(ScanLegOutcome.PageShapeChanged)]
    [InlineData(ScanLegOutcome.ReleaseRefused)]
    [InlineData(ScanLegOutcome.Transient)]
    public async Task A_run_that_could_not_see_settles_nothing(ScanLegOutcome outcome)
    {
        // The mistake that would matter. A company that rate-limited us said nothing about
        // whether somebody is listed, and reading silence as absence would tell a person
        // their data was deleted when nobody ever looked.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(-1));
        var scanId = await RunAsync(outcome, recorded: 0);

        Assert.Equal(0, await ResolveAsync(scanId));

        Assert.Equal("awaiting_broker_response", await StatusOfAsync(requestId));
    }

    [Fact]
    public async Task A_confirmed_demand_takes_its_findings_with_it()
    {
        // Two answers to one question on one screen is worse than either. A finding still
        // saying "requested" after the demand is confirmed is exactly that.
        var requestId = await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(30));
        var exposureId = await FindingAsync(ExposureStatus.Requested);
        var scanId = await RunAsync(ScanLegOutcome.NothingFound, recorded: 0);

        await ResolveAsync(scanId);

        Assert.Equal("removed", await StatusOfAsync(requestId));
        Assert.Equal("removed", await ExposureStatusOfAsync(exposureId));
    }

    [Fact]
    public async Task A_dismissed_finding_is_left_alone()
    {
        // Somebody said that was not them. A run confirming a removal has nothing to say
        // about a false positive, and reviving it as "removed" would put it back on a screen
        // it was deliberately taken off.
        await AwaitingDemandAsync(deadlineIn: TimeSpan.FromDays(30));
        var exposureId = await FindingAsync(ExposureStatus.Dismissed);
        var scanId = await RunAsync(ScanLegOutcome.NothingFound, recorded: 0);

        await ResolveAsync(scanId);

        Assert.Equal("dismissed", await ExposureStatusOfAsync(exposureId));
    }

    [Fact]
    public async Task A_demand_for_another_company_is_not_answered_by_this_one()
    {
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Other {_suffix}', 'other-{_suffix}.test', 'email', 45, true);
             """);

        var otherId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = 'other-{_suffix}.test'");

        var requestId = await AwaitingDemandAsync(TimeSpan.FromDays(30), brokerId: otherId);
        var scanId = await RunAsync(ScanLegOutcome.NothingFound, recorded: 0);

        await ResolveAsync(scanId);

        Assert.Equal("awaiting_broker_response", await StatusOfAsync(requestId));

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.removal_request WHERE broker_id = '{otherId}';
             DELETE FROM public.broker WHERE id = '{otherId}';
             """);
    }

    private async Task<int> ResolveAsync(Guid scanId)
    {
        await using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(_tenantId);

        return await scope.ServiceProvider
            .GetRequiredService<RemovalVerification>()
            .ResolveAsync(scanId, TestContext.Current.CancellationToken);
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbrPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Core"] = postgres.ConnectionString,
                })
                .Build());

        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped(provider => new RemovalVerification(
            provider.GetRequiredService<DbrDbContext>(),
            _clock,
            NullLogger<RemovalVerification>.Instance));

        return services.BuildServiceProvider();
    }

    private async Task<Guid> AwaitingDemandAsync(TimeSpan deadlineIn, Guid? brokerId = null)
    {
        var id = Guid.NewGuid();
        var deadline = Now.Add(deadlineIn);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.removal_request
                 (id, tenant_id, privacy_profile_id, broker_id, request_type, status, strategy,
                  attempt, deadline_source, deadline_at, created_at)
                 VALUES ('{id}', '{_tenantId}', '{_profileId}', '{brokerId ?? _brokerId}',
                         'delete', 'awaiting_broker_response', 'manual_email', 1,
                         'operational_default', '{deadline:O}', now());
             """);

        return id;
    }

    /// <summary>
    /// A finding discovered before the clock this test runs on.
    /// </summary>
    /// <remarks>
    /// The schema refuses a verification dated before the discovery it verifies, which is
    /// right and is why the fixture cannot use the wall clock while the code under test uses
    /// a fixed one — the two would disagree about which came first.
    /// </remarks>
    private async Task<Guid> FindingAsync(ExposureStatus status)
    {
        var id = Guid.NewGuid();
        var scanId = await ScanRowAsync();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.exposure
                 (id, tenant_id, scan_id, privacy_profile_id, broker_id, status, confidence,
                  discovered_at)
                 VALUES ('{id}', '{_tenantId}', '{scanId}', '{_profileId}', '{_brokerId}',
                         '{MonitoringVocabulary.ToWire(status)}', 0.9, '{Now.AddDays(-1):O}');
             """);

        return id;
    }

    private async Task<Guid> ScanRowAsync()
    {
        var scanId = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.scan
                 (id, tenant_id, privacy_profile_id, trigger, status, requested_at)
                 VALUES ('{scanId}', '{_tenantId}', '{_profileId}', 'manual', 'completed', now());
             """);

        return scanId;
    }

    private async Task<Guid> RunAsync(ScanLegOutcome outcome, int recorded)
    {
        var scanId = await ScanRowAsync();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.scan_leg
                 (tenant_id, scan_id, broker_id, attempt_number, planned_at, completed_at,
                  outcome, candidates_found, candidates_recorded)
                 VALUES ('{_tenantId}', '{scanId}', '{_brokerId}', 1, now(), now(),
                         '{MonitoringVocabulary.ToWire(outcome)}', {recorded}, {recorded});
             """);

        return scanId;
    }

    private async Task<string?> StatusOfAsync(Guid requestId) =>
        await postgres.QueryAsOwnerAsync<string?>(
            $"SELECT status FROM public.removal_request WHERE id = '{requestId}'");

    private async Task<string?> ExposureStatusOfAsync(Guid exposureId) =>
        await postgres.QueryAsOwnerAsync<string?>(
            $"SELECT status FROM public.exposure WHERE id = '{exposureId}'");

    /// <summary>
    /// A clock that does not move, so a deadline is where the test put it.
    /// </summary>
    /// <remarks>
    /// The whole subject here is which side of a deadline "now" falls on, and a real clock
    /// would make that a property of how long the test took to reach the assertion.
    /// </remarks>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
