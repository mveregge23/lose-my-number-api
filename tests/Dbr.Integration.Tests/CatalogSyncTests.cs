// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.CatalogSync;
using Dbr.Domain.Catalog;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Applying curated files to a real database.
/// </summary>
/// <remarks>
/// <para>
/// The reader has its own tests and never touches a database. What only shows up here is
/// the half the <c>source</c> column exists for: that the sync can retract what it wrote,
/// and cannot touch what it did not.
/// </para>
/// <para>
/// Every row these use is this class's own, under codes nothing else reads, so the
/// shipped catalog sitting in the same table is neither disturbed nor relied upon.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class CatalogSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Managed => $"SYNC{_suffix}";

    private string Owned => $"MINE{_suffix}";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() =>
        await postgres.ExecuteAsOwnerAsync(
            $"DELETE FROM public.legal_basis WHERE code IN ('{Managed}', '{Owned}');");

    [Fact]
    public async Task A_file_becomes_a_row_the_catalog_owns()
    {
        await RunAsync([Row(Managed, 45, DeadlineUnit.Calendar)]);

        Assert.Equal(45, await DaysAsync(Managed));
        Assert.Equal("catalog", await SourceAsync(Managed));
    }

    [Fact]
    public async Task A_changed_file_updates_the_row_rather_than_colliding_with_it()
    {
        await RunAsync([Row(Managed, 45, DeadlineUnit.Calendar)]);

        // The same regime, re-read and corrected. The natural key is unchanged, so this
        // is the ordinary case rather than a conflict.
        await RunAsync([Row(Managed, 30, DeadlineUnit.Business)]);

        Assert.Equal(30, await DaysAsync(Managed));
        Assert.Equal("business", await UnitAsync(Managed));
        Assert.Equal(1, await CountAsync(Managed));
    }

    [Fact]
    public async Task A_file_that_goes_away_takes_its_row_with_it()
    {
        // The case the source column exists for. A regime read wrongly and corrected has
        // to stop governing requests when somebody deletes its file, rather than
        // lingering until every install is cleaned up by hand.
        await RunAsync([Row(Managed, 45, DeadlineUnit.Calendar)]);
        Assert.Equal(1, await CountAsync(Managed));

        var result = await RunAsync([]);

        Assert.Equal(0, await CountAsync(Managed));
        Assert.True(result.Retracted >= 1);
    }

    [Fact]
    public async Task A_row_this_instance_owns_survives_a_sync_that_describes_it()
    {
        // An operator's own reading of a regime the shared catalog also carries. The
        // sync reports it and changes nothing — being overruled by a deploy is exactly
        // what the design says must not happen.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days, extension_days,
                  deadline_unit, verification_level, citation_url, reviewed_at, reviewed_by, source)
                 VALUES ('{Owned}', 'delete', 'US-CA', 7, 0, 'calendar', 'basic',
                         'https://example.test/mine', now(), 'the operator', 'local');
             """);

        var result = await RunAsync([Row(Owned, 45, DeadlineUnit.Calendar)]);

        Assert.Equal(7, await DaysAsync(Owned));
        Assert.Equal("local", await SourceAsync(Owned));
        Assert.Contains(result.LeftAlone, claimed => claimed.Contains(Owned, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_this_instance_owns_is_not_retracted_either()
    {
        // The other direction, and the one that would be silent: a catalog describing
        // nothing must not take an operator's own rows with it.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.legal_basis
                 (code, request_type, residency_scope, response_deadline_days, extension_days,
                  deadline_unit, verification_level, citation_url, reviewed_at, reviewed_by, source)
                 VALUES ('{Owned}', 'delete', 'US-CA', 7, 0, 'calendar', 'basic',
                         'https://example.test/mine', now(), 'the operator', 'local');
             """);

        await RunAsync([]);

        Assert.Equal(1, await CountAsync(Owned));
    }

    [Fact]
    public async Task Retracting_a_regime_brokers_are_confirmed_against_is_refused()
    {
        // The schema forbids dropping a regime with confirmations against it, because
        // those are somebody's reviewed judgement that the statute applies. The sync
        // reports that plainly rather than failing with a constraint name.
        await RunAsync([Row(Managed, 45, DeadlineUnit.Calendar)]);

        var domain = $"sync-{_suffix}.test";

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days)
                 VALUES ('Confirmer {_suffix}', '{domain}', 'email', 30);
             INSERT INTO public.broker_legal_basis (broker_id, legal_basis_id, confirmed_by)
                 SELECT b.id, l.id, 'counsel'
                 FROM public.broker b, public.legal_basis l
                 WHERE b.domain = '{domain}' AND l.code = '{Managed}';
             """);

        await Assert.ThrowsAsync<CatalogRetractionBlockedException>(() => RunAsync([]));

        // And nothing was half-applied: the whole run is one transaction.
        Assert.Equal(1, await CountAsync(Managed));

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker_legal_basis
                 WHERE broker_id IN (SELECT id FROM public.broker WHERE domain = '{domain}');
             DELETE FROM public.broker WHERE domain = '{domain}';
             """);
    }

    private CatalogRow Row(string code, int days, DeadlineUnit unit) =>
        new(
            code,
            LegalRequestType.Delete,
            "US-CA",
            days,
            0,
            unit,
            VerificationLevel.Basic,
            $"https://example.test/{code}",
            new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero),
            "@mveregge23");

    /// <summary>
    /// Runs the sync over exactly the rows given, plus whatever the shipped catalog
    /// holds — so a retraction here never proposes removing the real content.
    /// </summary>
    private async Task<CatalogSyncResult> RunAsync(IReadOnlyList<CatalogRow> rows)
    {
        var shipped = CatalogReader.Read(typeof(CatalogRow).Assembly).Rows;

        return await new CatalogSyncRunner(postgres.ConnectionString)
            .RunAsync([.. shipped, .. rows], TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync(string code) =>
        await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.legal_basis WHERE code = '{code}'");

    private async Task<int> DaysAsync(string code) =>
        await postgres.QueryAsOwnerAsync<int>(
            $"SELECT response_deadline_days FROM public.legal_basis WHERE code = '{code}'");

    private async Task<string?> SourceAsync(string code) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT source FROM public.legal_basis WHERE code = '{code}'");

    private async Task<string?> UnitAsync(string code) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT deadline_unit FROM public.legal_basis WHERE code = '{code}'");
}
