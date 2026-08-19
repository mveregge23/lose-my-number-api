// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// The catalog routes as anybody meets them: over HTTP, with no token at all.
/// </summary>
/// <remarks>
/// <para>
/// The service underneath is a few filtered queries and the boundary tests already prove
/// the rows are the same rows for everybody. What only shows up here is whether these
/// routes actually answer without a token — which is invisible from inside the service,
/// since a group that quietly required one would pass every test written against it —
/// and which fields reach the wire.
/// </para>
/// <para>
/// Assertions are scoped to the rows this class seeds rather than to the whole catalog,
/// because the listing returns everything active and another test's leftovers would
/// otherwise decide whether this one passes.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class CatalogEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _deleteBasisId;

    private Guid _optOutBasisId;

    private Guid _acmeId;

    private Guid _dormantId;

    private string AcmeDomain => $"acme-{_suffix}.test";

    private string MailOnlyDomain => $"mail-only-{_suffix}.test";

    private string DormantDomain => $"dormant-{_suffix}.test";

    private string DeleteCode => $"D{_suffix}";

    private string OptOutCode => $"O{_suffix}";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('Acme Data {_suffix}', '{AcmeDomain}', 'webform', 45, true),
                        ('Mail Only {_suffix}', '{MailOnlyDomain}', 'email', 30, true),
                        ('Dormant {_suffix}', '{DormantDomain}', 'email', 30, false);

             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days, extension_days,
                  verification_level, citation_url, reviewed_at, reviewed_by)
                 VALUES ('{DeleteCode}', 'delete', 'US-CA', 45, 45, 'basic',
                         'https://example.test/delete', now(), 'counsel'),
                        ('{OptOutCode}', 'opt_out_sale', 'US-VA', 30, 0, 'none',
                         'https://example.test/opt-out', now(), 'counsel');

             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT b.id, l.id, 'counsel'
                 FROM public.broker b, public.legal_basis l
                 WHERE b.domain = '{AcmeDomain}' AND l.code = '{DeleteCode}';
             """);

        _deleteBasisId = await IdOfAsync($"SELECT id FROM public.legal_basis WHERE code = '{DeleteCode}'");
        _optOutBasisId = await IdOfAsync($"SELECT id FROM public.legal_basis WHERE code = '{OptOutCode}'");
        _acmeId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{AcmeDomain}'");
        _dormantId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{DormantDomain}'");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker_legal_basis
                 WHERE broker_id IN (SELECT id FROM public.broker WHERE domain LIKE '%{_suffix}.test');
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             DELETE FROM public.legal_basis WHERE code IN ('{DeleteCode}', '{OptOutCode}');
             """);
    }

    [Fact]
    public async Task Every_catalog_route_answers_without_a_token()
    {
        // The one thing about these routes that cannot be seen from inside the service.
        // Every other group in this API declares RequireAuthorization, so this asserts
        // the deliberate absence rather than trusting that nobody added it back.
        var routes = new[]
        {
            "/api/v1/brokers",
            $"/api/v1/brokers/{_acmeId}",
            "/api/v1/legal-basis",
            $"/api/v1/legal-basis/{_deleteBasisId}",
        };

        foreach (var route in routes)
        {
            var (status, _) = await _api.GetAsync(route, null);

            Assert.Equal(HttpStatusCode.OK, status);
        }
    }

    [Fact]
    public async Task The_listing_never_carries_this_instances_pacing()
    {
        // How hard this instance is willing to push a broker, and how many refusals stop
        // it trying, are in the same row as the public facts. They are not published:
        // the number of rate-limited answers that opens the breaker is only useful to
        // somebody who wants the breaker open.
        var broker = await FindListedAsync(AcmeDomain);

        foreach (var withheld in new[]
        {
            "maxConcurrency",
            "minDelayMs",
            "rateLimitThreshold",
            "cooldownMinutes",
            "formChangeThreshold",
        })
        {
            Assert.False(
                broker.TryGetProperty(withheld, out _),
                $"'{withheld}' reached the wire.");
        }

        // And the public half did arrive, so the test above is not passing because the
        // response is empty.
        Assert.Equal(AcmeDomain, broker.GetProperty("domain").GetString());
        Assert.Equal("webform", broker.GetProperty("removalMethod").GetString());
        Assert.Equal("alias_preferred", broker.GetProperty("emailContactMode").GetString());
    }

    [Fact]
    public async Task A_courtesy_target_is_named_as_one()
    {
        // A statutory deadline and a broker's own target are different promises, and a
        // field called 'slaDays' beside a statute's 'responseDeadlineDays' invites a
        // client to render them as the same kind of thing.
        var broker = await FindListedAsync(AcmeDomain);

        Assert.Equal(45, broker.GetProperty("operationalSlaDays").GetInt32());
        Assert.False(broker.TryGetProperty("slaDays", out _));
    }

    [Fact]
    public async Task Nothing_has_verified_a_new_entry_and_the_wire_says_so()
    {
        // Null rather than the moment of insert. Never checked against the live site and
        // checked long ago are different problems, and only one of them is nobody's
        // fault.
        var broker = await FindListedAsync(AcmeDomain);

        Assert.Equal(JsonValueKind.Null, broker.GetProperty("catalogVerifiedAt").ValueKind);
    }

    [Fact]
    public async Task A_deactivated_entry_is_not_offered_but_is_not_denied_either()
    {
        var (_, listed) = await _api.GetAsync("/api/v1/brokers", null);

        Assert.DoesNotContain(
            Mine(listed, "brokers"),
            broker => broker.GetProperty("domain").GetString() == DormantDomain);

        // The detail still answers, and says which it is. Somebody holding a link to a
        // broker this instance stopped working should be told that, not told the company
        // was never in the catalog.
        var (status, detail) = await _api.GetAsync($"/api/v1/brokers/{_dormantId}", null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(detail.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task A_broker_detail_carries_who_confirmed_the_statute_and_when()
    {
        // The confirmation is the part no code could work out — applicability turns on
        // thresholds this system cannot check — so it travels with the regime rather
        // than being summarised into a deadline.
        var (_, detail) = await _api.GetAsync($"/api/v1/brokers/{_acmeId}", null);

        var regime = Assert.Single(detail.GetProperty("legalBases").EnumerateArray());

        Assert.Equal(DeleteCode, regime.GetProperty("code").GetString());
        Assert.Equal("delete", regime.GetProperty("requestType").GetString());
        Assert.Equal("US-CA", regime.GetProperty("residencyScope").GetString());
        Assert.Equal(45, regime.GetProperty("responseDeadlineDays").GetInt32());
        Assert.Equal(45, regime.GetProperty("extensionDays").GetInt32());
        Assert.Equal("basic", regime.GetProperty("verificationLevel").GetString());
        Assert.Equal("https://example.test/delete", regime.GetProperty("citationUrl").GetString());
        Assert.Equal("counsel", regime.GetProperty("reviewedBy").GetString());
        Assert.Equal("counsel", regime.GetProperty("confirmedBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, regime.GetProperty("confirmedAt").ValueKind);
    }

    [Fact]
    public async Task A_broker_nobody_has_confirmed_a_statute_against_says_so_with_an_empty_list()
    {
        // Empty is an answer: this removal would get the courtesy target. It is not a
        // claim that no statute reaches the company, and a missing field would read as
        // one.
        var mailOnlyId = await IdOfAsync($"SELECT id FROM public.broker WHERE domain = '{MailOnlyDomain}'");

        var (_, detail) = await _api.GetAsync($"/api/v1/brokers/{mailOnlyId}", null);

        Assert.Empty(detail.GetProperty("legalBases").EnumerateArray());
    }

    [Fact]
    public async Task Brokers_can_be_narrowed_to_how_they_take_a_request()
    {
        var (_, body) = await _api.GetAsync("/api/v1/brokers?removalMethod=email", null);

        var mine = Mine(body, "brokers").ToList();

        Assert.Contains(mine, broker => broker.GetProperty("domain").GetString() == MailOnlyDomain);
        Assert.DoesNotContain(mine, broker => broker.GetProperty("domain").GetString() == AcmeDomain);
    }

    [Fact]
    public async Task Brokers_can_be_narrowed_to_a_regime_somebody_confirmed()
    {
        var (_, confirmed) = await _api.GetAsync($"/api/v1/brokers?legalBasisId={_deleteBasisId}", null);

        var mine = Mine(confirmed, "brokers").ToList();

        Assert.Contains(mine, broker => broker.GetProperty("domain").GetString() == AcmeDomain);
        Assert.DoesNotContain(mine, broker => broker.GetProperty("domain").GetString() == MailOnlyDomain);

        // A regime nobody has confirmed against anything answers with nothing, which is
        // the honest answer — a confirmation is a judgement somebody makes, so its
        // absence is a fact about the catalog rather than about the statute.
        var (_, unconfirmed) = await _api.GetAsync($"/api/v1/brokers?legalBasisId={_optOutBasisId}", null);

        Assert.Empty(Mine(unconfirmed, "brokers"));
    }

    [Fact]
    public async Task The_two_broker_filters_narrow_together_rather_than_replacing_each_other()
    {
        // The realistic question is both at once, and an implementation that let the
        // last filter win would still pass each of the tests above.
        var (_, body) = await _api.GetAsync(
            $"/api/v1/brokers?legalBasisId={_deleteBasisId}&removalMethod=email",
            null);

        Assert.Empty(Mine(body, "brokers"));
    }

    [Fact]
    public async Task Regimes_can_be_narrowed_by_who_they_protect_and_what_they_grant()
    {
        var (_, byScope) = await _api.GetAsync("/api/v1/legal-basis?residencyScope=US-CA", null);

        Assert.Contains(Mine(byScope, "legalBases"), basis => Code(basis) == DeleteCode);
        Assert.DoesNotContain(Mine(byScope, "legalBases"), basis => Code(basis) == OptOutCode);

        var (_, byType) = await _api.GetAsync("/api/v1/legal-basis?requestType=opt_out_sale", null);

        Assert.Contains(Mine(byType, "legalBases"), basis => Code(basis) == OptOutCode);
        Assert.DoesNotContain(Mine(byType, "legalBases"), basis => Code(basis) == DeleteCode);
    }

    [Fact]
    public async Task A_region_is_matched_however_somebody_typed_it()
    {
        // Region codes are stored upper-cased and a client is not going to be careful
        // about it. Normalizing here is what keeps a lower-cased query from reading as
        // "no statute covers you".
        var (status, body) = await _api.GetAsync("/api/v1/legal-basis?residencyScope=+us-ca+", null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(Mine(body, "legalBases"), basis => Code(basis) == DeleteCode);
    }

    [Theory]
    [InlineData("/api/v1/brokers?removalMethod=carrier-pigeon")]
    [InlineData("/api/v1/brokers?legalBasisId=CCPA")]
    [InlineData("/api/v1/legal-basis?residencyScope=California")]
    [InlineData("/api/v1/legal-basis?requestType=forget-me")]
    public async Task A_filter_value_that_is_not_one_of_the_values_is_refused(string route)
    {
        // Not ignored, and not treated as matching nothing. Both of those are answers
        // somebody would believe: one looks like the whole catalog, the other looks like
        // an empty one.
        var (status, body) = await _api.GetAsync(route, null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    [Theory]
    [InlineData("/api/v1/brokers?removalMethod=")]
    [InlineData("/api/v1/brokers?legalBasisId=")]
    [InlineData("/api/v1/legal-basis?residencyScope=")]
    [InlineData("/api/v1/legal-basis?requestType=")]
    public async Task An_empty_filter_is_a_filter_nobody_set(string route)
    {
        // What a form control with nothing selected sends. Refusing it would make every
        // client that has one special-case building its own query string.
        var (status, _) = await _api.GetAsync(route, null);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task An_id_the_catalog_does_not_have_is_a_404_rather_than_an_empty_answer()
    {
        var missing = Guid.NewGuid();

        var (broker, _) = await _api.GetAsync($"/api/v1/brokers/{missing}", null);
        var (basis, _) = await _api.GetAsync($"/api/v1/legal-basis/{missing}", null);

        Assert.Equal(HttpStatusCode.NotFound, broker);
        Assert.Equal(HttpStatusCode.NotFound, basis);
    }

    [Fact]
    public async Task Something_that_is_not_an_id_at_all_never_reaches_the_catalog()
    {
        // The route constraint answers this, which is worth pinning: without it the
        // parameter would bind as a default and a typo would read as a missing broker.
        var (status, _) = await _api.GetAsync("/api/v1/brokers/not-an-id", null);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task The_listing_is_in_an_order_somebody_chose()
    {
        // Without an ORDER BY this passes on most days and fails on the one where the
        // planner picks a different scan.
        var (_, body) = await _api.GetAsync("/api/v1/brokers", null);

        var names = Mine(body, "brokers")
            .Select(broker => broker.GetProperty("name").GetString()!)
            .ToList();

        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    private async Task<JsonElement> FindListedAsync(string domain)
    {
        var (_, body) = await _api.GetAsync("/api/v1/brokers", null);

        return Assert.Single(
            Mine(body, "brokers"),
            broker => broker.GetProperty("domain").GetString() == domain);
    }

    /// <summary>Only the rows this class seeded, whatever else the catalog holds.</summary>
    private IEnumerable<JsonElement> Mine(JsonElement body, string property) =>
        body.GetProperty(property)
            .EnumerateArray()
            .Where(item => item.ToString().Contains(_suffix, StringComparison.Ordinal));

    private static string? Code(JsonElement basis) => basis.GetProperty("code").GetString();

    private async Task<Guid> IdOfAsync(string sql) =>
        await postgres.QueryAsOwnerAsync<Guid>(sql);
}
