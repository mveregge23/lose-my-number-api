// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dbr.Integration.Tests;

/// <summary>
/// The catalog: shared by everybody, owned by nobody, and read-only to the application.
/// </summary>
/// <remarks>
/// Every other table in this schema is asserted to keep one tenant's rows away from
/// another. These assert the opposite, because the mistake available here is the
/// opposite one: a catalog that got scoped would show every tenant an empty list, every
/// removal would quietly fall back to an operational deadline, and nothing would look
/// broken except the answers.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class BrokerCatalogTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _domain = $"broker-{Guid.NewGuid():N}.test";

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days)
                 VALUES ('Acme Data', '{_domain}', 'webform', 45);

             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days,
                  verification_level, citation_url, reviewed_at, reviewed_by)
                 VALUES ('TESTCCPA', 'opt_out_sale', 'US-CA', 45, 'basic',
                         'https://example.test/statute', now(), 'counsel');

             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT b.id, l.id, 'counsel'
                 FROM public.broker b, public.legal_basis l
                 WHERE b.domain = '{_domain}' AND l.code = 'TESTCCPA';
             """);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker_legal_basis
                 WHERE broker_id IN (SELECT id FROM public.broker WHERE domain = '{_domain}');
             DELETE FROM public.broker WHERE domain = '{_domain}';
             DELETE FROM public.legal_basis WHERE code = 'TESTCCPA';
             """);

        await _services.DisposeAsync();
    }

    [Fact]
    public async Task The_catalog_is_the_same_catalog_for_everybody()
    {
        // The inverse of every other boundary test here, and the one that would catch
        // this table being opted into row-level security by mistake.
        Assert.Equal("Acme Data", await BrokerNameVisibleToAsync(Alice));
        Assert.Equal("Acme Data", await BrokerNameVisibleToAsync(Bob));

        // Including a connection carrying no tenant at all, which for every scoped
        // table is the case that must return nothing.
        Assert.Equal("Acme Data", await BrokerNameVisibleToAsync(null));
    }

    [Theory]
    [InlineData("INSERT INTO public.broker (name, domain, removal_method, sla_days) VALUES ('x', 'x.test', 'email', 30)")]
    [InlineData("UPDATE public.broker SET sla_days = 1")]
    [InlineData("DELETE FROM public.broker")]
    [InlineData("UPDATE public.legal_basis SET response_deadline_days = 1")]
    [InlineData("DELETE FROM public.legal_basis")]
    [InlineData("DELETE FROM public.broker_legal_basis")]
    public async Task The_application_role_cannot_write_the_catalog(string statement)
    {
        // The role that serves requests reads the statute it computes a deadline from
        // and cannot edit it. Curated content arrives by a reviewed path instead.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync($"SET ROLE dbr_app; {statement};"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_values_with_underscores_survive_the_round_trip()
    {
        // The mapping most likely to be quietly wrong: lower-casing the C# spelling
        // gives 'optoutsale' and 'aliaspreferred', and the column would reject both —
        // so this is the test that fails if either conversion is ever derived rather
        // than spelled out.
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var basis = await context.Set<LegalBasis>()
            .SingleAsync(b => b.Code == "TESTCCPA", TestContext.Current.CancellationToken);

        Assert.Equal(LegalRequestType.OptOutSale, basis.RequestType);
        Assert.Equal(VerificationLevel.Basic, basis.VerificationLevel);

        var broker = await context.Set<Broker>()
            .SingleAsync(b => b.Domain == _domain, TestContext.Current.CancellationToken);

        Assert.Equal(RemovalMethod.WebForm, broker.RemovalMethod);
        Assert.Equal(EmailContactMode.AliasPreferred, broker.EmailContactMode);
    }

    [Fact]
    public async Task A_new_broker_is_paced_gently_until_somebody_says_otherwise()
    {
        // The defaults matter because a row added without thinking about pacing is the
        // common case. Guessing high would make this service look like something a
        // broker should block.
        using var scope = PostgresFixture.ScopeFor(_services, Alice);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var broker = await context.Set<Broker>()
            .SingleAsync(b => b.Domain == _domain, TestContext.Current.CancellationToken);

        Assert.Equal(1, broker.MaxConcurrency);
        Assert.True(broker.MinDelayMs > 0);
        Assert.True(broker.Active);

        // Never checked against the live site is not the same as checked long ago.
        Assert.Null(broker.CatalogVerifiedAt);
    }

    [Theory]
    [InlineData("citation_url", "''")]
    [InlineData("reviewed_by", "'   '")]
    public async Task A_legal_basis_that_cannot_say_where_it_came_from_is_refused(
        string column,
        string value)
    {
        // Provenance is required rather than decorative. A row with the wrong deadline
        // and no reviewer misinforms somebody about their legal position, and an absent
        // row would have been safer — it falls back to a deadline honestly labelled as
        // a courtesy.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 INSERT INTO public.legal_basis
                     (code, request_type, residency_scope, response_deadline_days,
                      verification_level, citation_url, reviewed_at, reviewed_by)
                     VALUES ('BAD', 'delete', 'US-CA', 30, 'none',
                             'https://example.test/x', now(), 'counsel')
                     ON CONFLICT DO NOTHING;
                 UPDATE public.legal_basis SET {column} = {value} WHERE code = 'BAD';
                 """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);

        await postgres.ExecuteAsOwnerAsync("DELETE FROM public.legal_basis WHERE code = 'BAD';");
    }

    [Fact]
    public async Task A_residency_scope_that_is_not_a_region_code_is_refused()
    {
        // Compared directly against what a profile records, so a second spelling here
        // would match nothing and read as a missing statute rather than as an error.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                """
                INSERT INTO public.legal_basis
                    (code, request_type, residency_scope, response_deadline_days,
                     verification_level, citation_url, reviewed_at, reviewed_by)
                    VALUES ('BAD', 'delete', 'California', 30, 'none',
                            'https://example.test/x', now(), 'counsel');
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task Two_catalog_rows_cannot_claim_one_domain()
    {
        // Two rows for one company would pace it as two lanes and let the same person
        // be submitted to it twice.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync(
                $"""
                 INSERT INTO public.broker (name, domain, removal_method, sla_days)
                     VALUES ('Acme Data, again', '{_domain}', 'email', 30);
                 """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_regime_that_brokers_are_confirmed_against_will_not_go_quietly()
    {
        // The confirmations are the reviewed judgement that a statute applies. Losing
        // them to a cascade is how a removal silently downgrades to an operational
        // deadline with nobody noticing.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsOwnerAsync("DELETE FROM public.legal_basis WHERE code = 'TESTCCPA';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_broker_leaving_the_catalog_takes_its_confirmations_with_it()
    {
        // The other direction, and cascading is right here: a confirmation is a claim
        // about a company, and it means nothing once the company is not in the catalog.
        var gone = $"gone-{Guid.NewGuid():N}.test";

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days)
                 VALUES ('Departing', '{gone}', 'email', 30);
             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT b.id, l.id, 'counsel'
                 FROM public.broker b, public.legal_basis l
                 WHERE b.domain = '{gone}' AND l.code = 'TESTCCPA';
             DELETE FROM public.broker WHERE domain = '{gone}';
             """);

        var orphans = await postgres.QueryAsOwnerAsync<long>(
            $"""
             SELECT count(*) FROM public.broker_legal_basis c
             WHERE NOT EXISTS (SELECT 1 FROM public.broker b WHERE b.id = c.broker_id)
             """);

        Assert.Equal(0L, orphans);
    }

    private async Task<string?> BrokerNameVisibleToAsync(Guid? tenantId)
    {
        using var scope = PostgresFixture.ScopeFor(_services, tenantId);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        return await context.Set<Broker>()
            .Where(broker => broker.Domain == _domain)
            .Select(broker => broker.Name)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }
}
