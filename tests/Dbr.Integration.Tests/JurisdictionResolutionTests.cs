// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// Which statute governs a request, and by when it has to be answered.
/// </summary>
/// <remarks>
/// <para>
/// Run against the real seeded catalog rather than fixtures invented here, because the
/// interesting cases are the ones the seed actually contains: a state whose opt-out clock
/// is counted in business days and whose deletion clock is not, and two states that agree
/// on the number of days and disagree on everything else.
/// </para>
/// <para>
/// The broker is this class's own, so its confirmations can be arranged without touching
/// rows another test reads.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class JurisdictionResolutionTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>A Monday, so every weekend a business-day count crosses is visible.</summary>
    private static readonly DateTimeOffset Received = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private ServiceProvider _services = null!;

    private Guid _brokerId;

    private string Domain => $"resolve-{_suffix}.test";

    /// <summary>A regime this class owns, so the seeded ones stay untouched.</summary>
    private string ShorterCode => $"SHORT{_suffix}";

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        // Forty days is deliberately between the statutory windows the seed carries, so
        // an operational fallback cannot be mistaken for a statutory answer that happens
        // to agree with it.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days)
                 VALUES ('Resolvable {_suffix}', '{Domain}', 'webform', 40);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{Domain}'");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker_legal_basis WHERE broker_id = '{_brokerId}';
             DELETE FROM public.broker WHERE id = '{_brokerId}';
             DELETE FROM public.legal_basis WHERE code = '{ShorterCode}';
             """);

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task A_broker_nobody_has_confirmed_a_statute_against_falls_back_to_its_own_target()
    {
        // No confirmations at all. The answer is the broker's courtesy target, labelled
        // as one — which is the honest reading of an incomplete catalog rather than a
        // failure of it.
        var resolved = await ResolveAsync("US-CA", LegalRequestType.Delete);

        Assert.Equal(DeadlineSource.OperationalDefault, resolved.Source);
        Assert.Null(resolved.LegalBasisId);
        Assert.Equal(Received.AddDays(40), resolved.DeadlineAt);
    }

    [Fact]
    public async Task A_confirmed_regime_governs_and_is_recorded()
    {
        await ConfirmAsync("CCPA", LegalRequestType.Delete);

        var resolved = await ResolveAsync("US-CA", LegalRequestType.Delete);

        Assert.Equal(DeadlineSource.Statutory, resolved.Source);
        Assert.Equal(await BasisIdAsync("CCPA", "delete"), resolved.LegalBasisId);

        // Forty-five calendar days from the Monday.
        Assert.Equal(Received.AddDays(45), resolved.DeadlineAt);
    }

    [Fact]
    public async Task Somebody_in_another_state_gets_the_fallback_from_the_same_broker()
    {
        // The confirmation is real, the regime is real, and it protects Californians.
        // Residency is what decides whether it reaches this person.
        await ConfirmAsync("CCPA", LegalRequestType.Delete);

        var resolved = await ResolveAsync("US-NY", LegalRequestType.Delete);

        Assert.Equal(DeadlineSource.OperationalDefault, resolved.Source);
        Assert.Null(resolved.LegalBasisId);
    }

    [Fact]
    public async Task Asking_for_something_the_regime_does_not_grant_gets_the_fallback()
    {
        // Confirmed for deletion only. An opt-out against the same broker, by the same
        // Californian, is governed by nothing — a deletion deadline is not an opt-out
        // deadline, which is why the request type is part of the intersection.
        await ConfirmAsync("CCPA", LegalRequestType.Delete);

        var resolved = await ResolveAsync("US-CA", LegalRequestType.OptOutSale);

        Assert.Equal(DeadlineSource.OperationalDefault, resolved.Source);
    }

    [Fact]
    public async Task A_business_day_window_resolves_to_a_business_day_date()
    {
        // California's opt-out clock, end to end: fifteen business days from a Monday is
        // the Monday three weeks later, not the Tuesday a fortnight out.
        await ConfirmAsync("CCPA", LegalRequestType.OptOutSale);

        var resolved = await ResolveAsync("US-CA", LegalRequestType.OptOutSale);

        Assert.Equal(DeadlineSource.Statutory, resolved.Source);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 9, 0, 0, TimeSpan.Zero), resolved.DeadlineAt);
        Assert.Equal(DayOfWeek.Monday, resolved.DeadlineAt.DayOfWeek);
    }

    [Fact]
    public async Task The_shortest_window_wins_as_a_date_rather_than_as_a_number()
    {
        // The case that decides whether "shortest responseDeadlineDays wins" was read as
        // arithmetic or as a date. This broker is confirmed against California's opt-out
        // rule (15 business days, which lands on 22 June) and a regime giving 18 calendar
        // days (which lands on 19 June). Eighteen is the larger number and the shorter
        // window, so a comparison on the counts alone picks the wrong statute — and the
        // wrong statute is what gets recorded as having governed the request.
        await SeedRegimeAsync(ShorterCode, "opt_out_sale", "US-CA", 18, "calendar");
        await ConfirmAsync("CCPA", LegalRequestType.OptOutSale);
        await ConfirmAsync(ShorterCode, LegalRequestType.OptOutSale);

        var resolved = await ResolveAsync("US-CA", LegalRequestType.OptOutSale);

        Assert.Equal(new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero), resolved.DeadlineAt);
        Assert.Equal(await BasisIdAsync(ShorterCode, "opt_out_sale"), resolved.LegalBasisId);
    }

    [Fact]
    public async Task Somebody_who_has_not_said_where_they_live_gets_the_fallback()
    {
        // A profile with no residency region has no jurisdiction to intersect against.
        // Guessing at one in order to promise a legal deadline is the failure worth
        // avoiding here.
        await ConfirmAsync("CCPA", LegalRequestType.Delete);

        var resolved = await ResolveAsync(null, LegalRequestType.Delete);

        Assert.Equal(DeadlineSource.OperationalDefault, resolved.Source);
        Assert.Null(resolved.LegalBasisId);
    }

    [Fact]
    public async Task A_region_typed_casually_still_finds_its_statute()
    {
        await ConfirmAsync("CCPA", LegalRequestType.Delete);

        var resolved = await ResolveAsync("  us-ca  ", LegalRequestType.Delete);

        Assert.Equal(DeadlineSource.Statutory, resolved.Source);
    }

    [Fact]
    public async Task A_broker_the_catalog_has_never_heard_of_is_refused()
    {
        // Not an operational default. Falling back would invent a courtesy target for a
        // company with no row to take one from.
        using var scope = PostgresFixture.ScopeFor(_services, Tenant);
        var resolver = scope.ServiceProvider.GetRequiredService<IJurisdictionResolver>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                Guid.NewGuid(),
                "US-CA",
                LegalRequestType.Delete,
                Received,
                TestContext.Current.CancellationToken));
    }

    private async Task<DeadlineResolution> ResolveAsync(string? region, LegalRequestType requestType)
    {
        using var scope = PostgresFixture.ScopeFor(_services, Tenant);
        var resolver = scope.ServiceProvider.GetRequiredService<IJurisdictionResolver>();

        return await resolver.ResolveAsync(
            _brokerId,
            region,
            requestType,
            Received,
            TestContext.Current.CancellationToken);
    }

    private async Task ConfirmAsync(string code, LegalRequestType requestType) =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT '{_brokerId}', l.id, 'counsel'
                 FROM public.legal_basis l
                 WHERE l.code = '{code}' AND l.request_type = '{Storage(requestType)}'
                 ON CONFLICT DO NOTHING;
             """);

    /// <summary>A regime this class owns, for the cases the seed does not contain.</summary>
    private async Task SeedRegimeAsync(
        string code,
        string requestType,
        string scope,
        int days,
        string unit) =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days, extension_days,
                  deadline_unit, verification_level, citation_url, reviewed_at, reviewed_by)
                 VALUES ('{code}', '{requestType}', '{scope}', {days}, 0, '{unit}', 'none',
                         'https://example.test/{code}', now(), 'test')
                 ON CONFLICT DO NOTHING;
             """);

    private async Task<Guid> BasisIdAsync(string code, string requestType) =>
        await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.legal_basis WHERE code = '{code}' AND request_type = '{requestType}'");

    private static string Storage(LegalRequestType requestType) =>
        CatalogVocabulary.ToWire(requestType);
}
