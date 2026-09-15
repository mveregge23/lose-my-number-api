// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.CatalogSync;
using Dbr.Domain.Catalog;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Applying company files to a real database.
/// </summary>
/// <remarks>
/// <para>
/// The reader has its own tests and never touches a database. What only shows up here is
/// what the <c>source</c> column exists for — that the sync can take back what it wrote
/// and cannot touch what it did not — plus the one place companies differ from regimes:
/// taking back means deactivating, because history points at a company and cannot be
/// left pointing at nothing.
/// </para>
/// <para>
/// Every row these use is this class's own, under ids and domains nothing else reads.
/// The domains are under a reserved name, which the reader would treat as a worked
/// example; the runner does not look, and that is deliberate — the reader is where that
/// rule lives, and these tests are about the runner.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class BrokerSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];

    private readonly Guid _managed = Guid.NewGuid();
    private readonly Guid _owned = Guid.NewGuid();

    private string ManagedDomain => $"managed-{_suffix}.test";

    private string OwnedDomain => $"owned-{_suffix}.test";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private string TestRegime => $"SYNC{_suffix.ToUpperInvariant()}";

    public async ValueTask DisposeAsync() =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker WHERE id IN ('{_managed}', '{_owned}');
             DELETE FROM public.legal_basis WHERE code = '{TestRegime}';
             """);

    [Fact]
    public async Task A_file_becomes_a_row_the_catalog_owns()
    {
        var result = await RunAsync([Company(_managed, ManagedDomain, minDelayMs: 2500)]);

        Assert.Equal(1, result.BrokersApplied);
        Assert.Equal("catalog", await ColumnAsync<string>("source"));
        Assert.Equal(2500, await ColumnAsync<int>("min_delay_ms"));
        Assert.True(await ColumnAsync<bool>("active"));
    }

    [Fact]
    public async Task A_changed_file_updates_the_row_under_the_same_id()
    {
        await RunAsync([Company(_managed, ManagedDomain)]);

        // The company renamed its site. The id is what everything binds to, so this is
        // the same row with a new domain rather than a new row beside an orphaned one.
        var renamed = $"renamed-{_suffix}.test";

        await RunAsync([Company(_managed, renamed, name: "Renamed", minDelayMs: 5000)]);

        Assert.Equal(renamed, await ColumnAsync<string>("domain"));
        Assert.Equal("Renamed", await ColumnAsync<string>("name"));
        Assert.Equal(5000, await ColumnAsync<int>("min_delay_ms"));
        Assert.Equal(1, await CountAsync(_managed));
    }

    [Fact]
    public async Task A_file_that_goes_away_deactivates_the_row_rather_than_deleting_it()
    {
        // Every scan that asked this company and every demand sent to it holds a key to
        // this row, so deleting it would be refused on any instance that ever used it.
        // The row stays and stops being used.
        await RunAsync([Company(_managed, ManagedDomain)]);

        var result = await RunAsync([]);

        Assert.Equal(1, result.BrokersRetracted);
        Assert.Equal(1, await CountAsync(_managed));
        Assert.False(await ColumnAsync<bool>("active"));

        // Taken back once. A count that rose again on every deploy would have an operator
        // reading "1 deactivated" for a company that left the catalog a year ago.
        Assert.Equal(0, (await RunAsync([])).BrokersRetracted);
    }

    [Fact]
    public async Task A_file_that_comes_back_reactivates_the_row()
    {
        await RunAsync([Company(_managed, ManagedDomain)]);
        await RunAsync([]);
        Assert.False(await ColumnAsync<bool>("active"));

        await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.True(await ColumnAsync<bool>("active"));
        Assert.Equal(1, await CountAsync(_managed));
    }

    [Fact]
    public async Task A_file_saying_inactive_is_applied_as_such()
    {
        // The catalog's way of saying a company closed or merged: the row exists for the
        // history that names it, and nothing new goes to it.
        await RunAsync([Company(_managed, ManagedDomain, active: false)]);

        Assert.False(await ColumnAsync<bool>("active"));
    }

    [Fact]
    public async Task A_company_this_instance_owns_under_the_files_id_is_left_alone()
    {
        await InsertOwnedAsync(_managed, ManagedDomain, minDelayMs: 9000);

        var result = await RunAsync([Company(_managed, ManagedDomain, minDelayMs: 100)]);

        Assert.Equal(0, result.BrokersApplied);
        Assert.Equal(9000, await ColumnAsync<int>("min_delay_ms"));
        Assert.Equal("local", await ColumnAsync<string>("source"));
        Assert.Contains(result.LeftAlone, claimed => claimed.Contains(ManagedDomain, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_company_this_instance_owns_under_the_files_domain_is_left_alone()
    {
        // The operator described the company before the catalog did, under their own id,
        // and their recipes bind to that id. The shared row would collide on the domain,
        // so it is not written, and the operator's row is not touched — reported, so
        // somebody wondering why the catalog's version never arrived can find out.
        await InsertOwnedAsync(_owned, ManagedDomain);

        var result = await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.Equal(0, result.BrokersApplied);
        Assert.Equal(0, await CountAsync(_managed));
        Assert.Equal(1, await CountAsync(_owned));
        Assert.Contains(result.LeftAlone, claimed => claimed.Contains(ManagedDomain, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_company_this_instance_owns_is_not_deactivated_by_a_catalog_that_omits_it()
    {
        await InsertOwnedAsync(_owned, OwnedDomain);

        await RunAsync([]);

        Assert.True(await ColumnAsync<bool>("active", _owned));
    }

    [Fact]
    public async Task Pacing_tuned_by_hand_on_a_catalog_row_is_tuned_back_until_the_row_is_taken_over()
    {
        // The file is the whole description of a catalog company, pacing included. An
        // operator who wants their own lane keeps it by taking the row — one column —
        // and the sync says so on every run thereafter.
        await RunAsync([Company(_managed, ManagedDomain, minDelayMs: 1000)]);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.broker SET min_delay_ms = 7000 WHERE id = '{_managed}';");
        await RunAsync([Company(_managed, ManagedDomain, minDelayMs: 1000)]);

        Assert.Equal(1000, await ColumnAsync<int>("min_delay_ms"));

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.broker SET min_delay_ms = 7000, source = 'local' WHERE id = '{_managed}';");
        var result = await RunAsync([Company(_managed, ManagedDomain, minDelayMs: 1000)]);

        Assert.Equal(7000, await ColumnAsync<int>("min_delay_ms"));
        Assert.Contains(result.LeftAlone, claimed => claimed.Contains(ManagedDomain, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_domain_another_catalog_company_still_holds_is_refused_and_nothing_is_applied()
    {
        // A retracted company keeps its domain while deactivated. If the catalog then
        // describes a different company under that domain, two rows would claim it,
        // which the schema refuses — and the refusal rolls back the whole run, regimes
        // included, rather than leaving half a catalog applied.
        await RunAsync([Company(_managed, ManagedDomain)]);

        var regime = $"SYNC{_suffix.ToUpperInvariant()}";

        await Assert.ThrowsAsync<CatalogSyncRefusedException>(() =>
            RunAsync([Company(_owned, ManagedDomain)], [Regime(regime)]));

        Assert.Equal(0, await CountAsync(_owned));
        Assert.Equal(
            0L,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.legal_basis WHERE code = '{regime}'"));
    }

    [Fact]
    public async Task A_confirmed_regime_becomes_one_confirmation_per_row_the_regime_has()
    {
        // The file names a statute; the statute is a row per request type it grants. A
        // regime that reaches a company reaches it for deletion and for opt-out alike, so
        // the reviewer's one judgement fans out rather than being asked three times.
        var result = await RunAsync([Company(_managed, ManagedDomain, subjectTo: "CCPA")]);

        var rows = await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM public.legal_basis WHERE code = 'CCPA'");

        Assert.Equal(rows, result.ConfirmationsApplied);
        Assert.Equal(rows, await ConfirmationsAsync("CCPA"));
        Assert.Equal("catalog", await ConfirmationColumnAsync<string>("source"));
        Assert.Equal("https://registry.example/CCPA", await ConfirmationColumnAsync<string>("evidence_url"));
    }

    [Fact]
    public async Task A_regime_dropped_from_the_file_takes_its_confirmations_with_it()
    {
        await RunAsync([Company(_managed, ManagedDomain, subjectTo: "CCPA")]);
        Assert.NotEqual(0L, await ConfirmationsAsync("CCPA"));

        var result = await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.Equal(0L, await ConfirmationsAsync("CCPA"));
        Assert.NotEqual(0, result.ConfirmationsRetracted);
    }

    [Fact]
    public async Task A_confirmation_this_instance_made_itself_is_left_alone_and_not_retracted()
    {
        // An operator confirmed the regime on their own evidence before the catalog got
        // to it. The catalog's evidence does not replace theirs, and a catalog that later
        // stops claiming the regime does not take their judgement with it.
        await RunAsync([Company(_managed, ManagedDomain)]);
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT '{_managed}', l.id, 'counsel' FROM public.legal_basis l WHERE l.code = 'CCPA';
             """);

        var described = await RunAsync([Company(_managed, ManagedDomain, subjectTo: "CCPA")]);

        Assert.Equal("counsel", await ConfirmationColumnAsync<string>("confirmed_by"));
        Assert.Null(await ConfirmationColumnAsync<string>("evidence_url"));
        Assert.Contains(described.LeftAlone, claimed => claimed.Contains("CCPA", StringComparison.Ordinal));

        var omitted = await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.Equal(0, omitted.ConfirmationsRetracted);
        Assert.NotEqual(0L, await ConfirmationsAsync("CCPA"));
    }

    [Fact]
    public async Task A_regime_and_every_claim_on_it_can_leave_the_files_in_one_run()
    {
        // Confirmations are taken back before regimes are, so the catalog can retract a
        // statute it once confirmed companies against without an operator having to
        // clear the confirmations by hand first — that refusal is for confirmations the
        // sync never owned.
        await RunAsync([Company(_managed, ManagedDomain, subjectTo: TestRegime)], [Regime(TestRegime)]);
        Assert.NotEqual(0L, await ConfirmationsAsync(TestRegime));

        var result = await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.NotEqual(0, result.ConfirmationsRetracted);
        Assert.NotEqual(0, result.Retracted);
        Assert.Equal(
            0L,
            await postgres.QueryAsOwnerAsync<long>(
                $"SELECT count(*) FROM public.legal_basis WHERE code = '{TestRegime}'"));
    }

    [Fact]
    public async Task A_regime_leaving_the_files_with_an_operators_confirmation_against_it_is_still_refused()
    {
        await RunAsync([Company(_managed, ManagedDomain)], [Regime(TestRegime)]);
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT '{_managed}', l.id, 'counsel' FROM public.legal_basis l WHERE l.code = '{TestRegime}';
             """);

        await Assert.ThrowsAsync<CatalogSyncRefusedException>(() => RunAsync([Company(_managed, ManagedDomain)]));

        Assert.NotEqual(0L, await ConfirmationsAsync(TestRegime));
    }

    [Fact]
    public async Task Confirmations_against_a_company_the_operator_took_over_are_not_touched()
    {
        // The company is theirs entirely once taken over. The catalog's own confirmations
        // against it stay exactly as written — neither updated nor removed — because
        // removing them would downgrade that operator's demands to courtesy deadlines as
        // a side effect of a decision about a different table.
        await RunAsync([Company(_managed, ManagedDomain, subjectTo: "CCPA")]);
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.broker SET source = 'local' WHERE id = '{_managed}';");

        var result = await RunAsync([Company(_managed, ManagedDomain)]);

        Assert.Equal(0, result.ConfirmationsRetracted);
        Assert.NotEqual(0L, await ConfirmationsAsync("CCPA"));
    }

    private async Task<long> ConfirmationsAsync(string code) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"""
             SELECT count(*) FROM public.broker_legal_basis bl
             JOIN public.legal_basis l ON l.id = bl.legal_basis_id
             WHERE bl.broker_id = '{_managed}' AND l.code = '{code}'
             """);

    private async Task<T?> ConfirmationColumnAsync<T>(string column) =>
        await postgres.QueryAsOwnerAsync<T>(
            $"SELECT {column} FROM public.broker_legal_basis WHERE broker_id = '{_managed}' LIMIT 1");

    private static BrokerRow Company(
        Guid id,
        string domain,
        string name = "Some Company",
        bool active = true,
        int minDelayMs = 1000,
        params string[] subjectTo) =>
        new(
            id,
            name,
            domain,
            RemovalMethod.Email,
            45,
            EmailContactMode.AliasPreferred,
            active,
            MaxConcurrency: 1,
            MinDelayMs: minDelayMs,
            RateLimitThreshold: 3,
            CooldownMinutes: 30,
            FormChangeThreshold: 3,
            [.. subjectTo.Select(code => new ConfirmationRow(
                code,
                $"https://registry.example/{code}",
                "@mveregge23",
                new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)))]);

    private static CatalogRow Regime(string code) =>
        new(
            code,
            LegalRequestType.Delete,
            "US-CA",
            45,
            0,
            DeadlineUnit.Calendar,
            VerificationLevel.Basic,
            $"https://example.test/{code}",
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            "@mveregge23");

    private async Task InsertOwnedAsync(Guid id, string domain, int minDelayMs = 1000) =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (id, name, domain, removal_method, sla_days, min_delay_ms)
                 VALUES ('{id}', 'Mine', '{domain}', 'email', 30, {minDelayMs});
             """);

    /// <summary>
    /// Runs the sync over exactly the companies given, plus whatever the shipped catalog
    /// holds of both kinds — so a retraction here never proposes removing real content.
    /// </summary>
    private async Task<CatalogSyncResult> RunAsync(
        IReadOnlyList<BrokerRow> brokers,
        IReadOnlyList<CatalogRow>? regimes = null)
    {
        var sync = typeof(BrokerRow).Assembly;
        var shippedRegimes = CatalogReader.Read(sync).Rows;
        var shippedBrokers = BrokerReader.Read(sync, shippedRegimes.Select(row => row.Code).ToHashSet(StringComparer.Ordinal)).Rows;

        return await new CatalogSyncRunner(postgres.ConnectionString)
            .RunAsync(
                [.. shippedRegimes, .. regimes ?? []],
                [.. shippedBrokers, .. brokers],
                TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync(Guid id) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.broker WHERE id = '{id}'");

    private async Task<T?> ColumnAsync<T>(string column, Guid? id = null) =>
        await postgres.QueryAsOwnerAsync<T>(
            $"SELECT {column} FROM public.broker WHERE id = '{id ?? _managed}'");
}
