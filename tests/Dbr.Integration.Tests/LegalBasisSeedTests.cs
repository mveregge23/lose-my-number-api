// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Catalog;
using Dbr.Domain.Regions;
using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The regimes the migrator seeds, as they land in a fresh database.
/// </summary>
/// <remarks>
/// <para>
/// These assert the shape of the content rather than re-stating it. A test repeating
/// "California is forty-five days" would fail the day a statute is amended, which is
/// the day the row is <em>supposed</em> to change — and it would say nothing about
/// whether the row is right, since it would be checking the seed against a copy of the
/// seed.
/// </para>
/// <para>
/// What is worth pinning is what the rest of the system needs to be true of any row
/// here: that a scope is a region code the resolution can match, that provenance points
/// somewhere reachable, and that the seed can be applied twice without doubling.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LegalBasisSeedTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] SeededCodes = ["CCPA", "VCDPA", "CPA", "CTDPA", "UCPA"];

    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task The_first_five_jurisdictions_are_in_the_catalog()
    {
        // The five §17 names as the highest-broker-volume jurisdictions to do first.
        var codes = await SeededAsync(basis => basis.Code);

        foreach (var code in SeededCodes)
        {
            Assert.Contains(code, codes);
        }
    }

    [Fact]
    public async Task Every_seeded_jurisdiction_can_at_least_be_asked_to_delete()
    {
        // Deletion is the right this product exists to exercise. A jurisdiction seeded
        // with only its opt-out rows would quietly resolve every removal to the
        // broker's courtesy target while looking covered.
        var bases = await AllSeededAsync();

        foreach (var code in SeededCodes)
        {
            Assert.Contains(
                bases,
                basis => basis.Code == code && basis.RequestType == LegalRequestType.Delete);
        }
    }

    [Fact]
    public async Task Every_scope_is_a_region_code_a_profile_could_match()
    {
        // The load-bearing one. Resolution intersects these against the region on a
        // profile with an exact match, so a scope spelled any other way is not a bad
        // row that fails — it is a statute that silently protects nobody.
        var bases = await AllSeededAsync();

        Assert.NotEmpty(bases);

        foreach (var basis in bases)
        {
            Assert.True(
                RegionCode.IsWellFormed(basis.ResidencyScope),
                $"{basis.Code} is scoped to '{basis.ResidencyScope}', which no profile region can equal.");
        }
    }

    [Fact]
    public async Task A_row_either_names_who_read_it_or_admits_nobody_has()
    {
        // Review happens jurisdiction by jurisdiction, so which rows are signed changes
        // over time and this deliberately does not pin that. What it pins is that there
        // is no third state: a reviewer is a handle somebody can look up, or the row
        // says outright that it is unreviewed. Anything else — a blank, a placeholder
        // that reads like a name, a half-edited value — is a row claiming provenance it
        // does not have, which is worse than an absent row because an absent row falls
        // back to a deadline honestly labelled a courtesy.
        var bases = await AllSeededAsync();

        foreach (var basis in bases)
        {
            var named = basis.ReviewedBy.StartsWith('@');
            var admitted = basis.ReviewedBy.Contains("unreviewed", StringComparison.OrdinalIgnoreCase);

            Assert.True(
                named ^ admitted,
                $"{basis.Code} is reviewed by '{basis.ReviewedBy}', which neither names somebody "
                + "nor says nobody has read it.");
        }
    }

    [Fact]
    public async Task Every_row_cites_somewhere_a_reader_could_actually_go()
    {
        // The column only refuses blank. A citation is the difference between a row
        // somebody can check and a number they have to take on faith, so "present" is
        // not the bar — it has to be a link.
        var bases = await AllSeededAsync();

        foreach (var basis in bases)
        {
            Assert.StartsWith("https://", basis.CitationUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_one_regime_counted_in_business_days_says_so()
    {
        // California's opt-out clock is the only one of the fifteen not counted in
        // calendar days, and it is stored as the statute prints it — fifteen, marked
        // business — rather than converted to a calendar figure that appears nowhere in
        // the source. Fifteen business days is most of a week longer than fifteen
        // calendar ones, so a row that lost the unit would read as a deadline six days
        // earlier than the law allows.
        var bases = await AllSeededAsync();

        foreach (var basis in bases.Where(basis =>
            basis.Code == "CCPA" && basis.RequestType != LegalRequestType.Delete))
        {
            Assert.Equal(DeadlineUnit.Business, basis.DeadlineUnit);
            Assert.Equal(15, basis.ResponseDeadlineDays);
        }

        // And nothing else claims business days, which is the half that would rot
        // quietly: a unit applied too widely stretches deadlines nobody asked to stretch.
        foreach (var basis in bases.Where(basis =>
            basis.Code != "CCPA" || basis.RequestType == LegalRequestType.Delete))
        {
            Assert.Equal(DeadlineUnit.Calendar, basis.DeadlineUnit);
        }
    }

    [Fact]
    public async Task A_deadline_is_a_real_window_and_an_extension_is_allowed_to_be_zero()
    {
        // Zero extension is a statement — this regime grants none — while a zero
        // deadline would be a row claiming the answer was already late on arrival.
        var bases = await AllSeededAsync();

        foreach (var basis in bases)
        {
            Assert.True(basis.ResponseDeadlineDays > 0, $"{basis.Code} has no answer window.");
            Assert.True(basis.ExtensionDays >= 0);
        }
    }

    [Fact]
    public async Task Seeding_the_same_regimes_again_changes_nothing()
    {
        // The script is written to be re-runnable against an instance that already has
        // its own reading of one of these, whose reviewer and date this seed cannot
        // improve on. DbUp would not run it twice; an operator applying the SQL by hand
        // is the case this protects.
        var before = await CountAsync();

        await postgres.ExecuteAsOwnerAsync(
            """
            INSERT INTO public.legal_basis
                (code, request_type, residency_scope, response_deadline_days, extension_days,
                 verification_level, citation_url, reviewed_at, reviewed_by)
                VALUES ('CCPA', 'delete', 'US-CA', 1, 0, 'none',
                        'https://example.test/not-the-seed', now(), 'somebody else')
                ON CONFLICT (code, request_type, residency_scope) DO NOTHING;
            """);

        Assert.Equal(before, await CountAsync());

        // And the row that was already there is untouched, which is the half that
        // matters: a conflict must not quietly overwrite somebody's reviewed reading.
        var california = await SingleAsync("CCPA", LegalRequestType.Delete);

        Assert.NotEqual(1, california.ResponseDeadlineDays);
        Assert.NotEqual("somebody else", california.ReviewedBy);
    }

    [Fact]
    public async Task The_seeded_regimes_are_readable_without_a_token()
    {
        // Seeded content is public content. This is the pairing of the two stories:
        // rows nobody can read are rows nobody can check.
        await using var factory = new DbrApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var (status, body) = await new ApiClient(client).GetAsync(
            "/api/v1/legal-basis?residencyScope=US-CA",
            null);

        Assert.Equal(HttpStatusCode.OK, status);

        var california = body.GetProperty("legalBases").EnumerateArray().ToList();

        Assert.Contains(california, basis => basis.GetProperty("code").GetString() == "CCPA");

        // The unit travels with the number. A client rendering "15 days" for a rule that
        // means fifteen business days would restate the deadline six days early, which is
        // the same error the column was added to stop — one layer further out.
        var optOut = Assert.Single(
            california,
            basis => basis.GetProperty("requestType").GetString() == "opt_out_sale");

        Assert.Equal(15, optOut.GetProperty("responseDeadlineDays").GetInt32());
        Assert.Equal("business", optOut.GetProperty("deadlineUnit").GetString());
    }

    private async Task<List<LegalBasis>> AllSeededAsync()
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        return await context.Set<LegalBasis>()
            .Where(basis => SeededCodes.Contains(basis.Code))
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<TValue>> SeededAsync<TValue>(
        System.Linq.Expressions.Expression<Func<LegalBasis, TValue>> select)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        return await context.Set<LegalBasis>()
            .Select(select)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<LegalBasis> SingleAsync(string code, LegalRequestType requestType)
    {
        using var scope = PostgresFixture.ScopeFor(_services, null);
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        return await context.Set<LegalBasis>()
            .SingleAsync(
                basis => basis.Code == code && basis.RequestType == requestType,
                TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync() =>
        await postgres.QueryAsOwnerAsync<long>("SELECT count(*) FROM public.legal_basis");
}
