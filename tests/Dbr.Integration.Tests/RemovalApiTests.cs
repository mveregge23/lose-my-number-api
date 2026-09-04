// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Opening a demand, reading it, and the two moves a person can make on it.
/// </summary>
/// <remarks>
/// <para>
/// Over HTTP throughout, because almost every claim here depends on what the token
/// established: the consent gate, the tenant boundary, and which identity a demand is made
/// for when the request does not name one. The schema's own guarantees are asserted
/// separately, by writing rows directly and watching the constraints reject them.
/// </para>
/// <para>
/// The company shapes matter. A form broker and a mailbox broker take different strategies,
/// and a postal one is the case this instance refuses outright — so all three are seeded.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class RemovalApiTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string RemovalsPath = "/api/v1/removal-requests";

    private const string ScansPath = "/api/v1/scans";

    private const string ConsentPath = "/api/v1/profile/consent";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private Guid _formBrokerId;

    private Guid _mailBrokerId;

    private Guid _postBrokerId;

    private string FormDomain => $"rma-form-{_suffix}.test";

    private string MailDomain => $"rma-mail-{_suffix}.test";

    private string PostDomain => $"rma-post-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('RMA Form {_suffix}', '{FormDomain}', 'webform', 45, true),
                        ('RMA Mail {_suffix}', '{MailDomain}', 'email', 30, true),
                        ('RMA Post {_suffix}', '{PostDomain}', 'postal', 60, true);
             """);

        _formBrokerId = await IdOfAsync(FormDomain);
        _mailBrokerId = await IdOfAsync(MailDomain);
        _postBrokerId = await IdOfAsync(PostDomain);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        // Children before parents throughout: a job references a request, a request
        // references a profile and a listing, and none of it cascades from the account.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.removal_job;
             DELETE FROM public.removal_request;
             DELETE FROM vault.exposure_source;
             DELETE FROM public.exposure;
             DELETE FROM public.scan_broker;
             DELETE FROM public.scan;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             """);
    }

    [Fact]
    public async Task Every_removal_route_refuses_a_request_with_no_token()
    {
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(RemovalsPath, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync($"{RemovalsPath}/{id}", null)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _api.GetAsync($"{RemovalsPath}/{id}/timeline", null)).Status);

        var (open, _) = await _api.PostAsync(RemovalsPath, new { brokerId = id, requestType = "delete" }, null);
        Assert.Equal(HttpStatusCode.Unauthorized, open);

        var (cancel, _) = await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, null);
        Assert.Equal(HttpStatusCode.Unauthorized, cancel);

        var (retry, _) = await _api.PostAsync($"{RemovalsPath}/{id}/retry", new { }, null);
        Assert.Equal(HttpStatusCode.Unauthorized, retry);
    }

    /// <summary>
    /// Agreeing to be searched for is not agreeing to be spoken for.
    /// </summary>
    /// <remarks>
    /// The account here has granted scanning and nothing else, which is exactly the
    /// separation the three consent scopes exist to make expressible.
    /// </remarks>
    [Fact]
    public async Task A_demand_needs_permission_that_scanning_does_not_give()
    {
        var account = await OpenAccountAsync(removals: false);

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(0, await CountAsync());
    }

    /// <summary>
    /// The consent check runs before anything that could confirm an id is real.
    /// </summary>
    [Fact]
    public async Task Permission_is_checked_before_the_company_is_looked_up()
    {
        var account = await OpenAccountAsync(removals: false);

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = Guid.NewGuid(), requestType = "delete" },
            account.Token);

        // Forbidden rather than the 400 an unknown broker gets, so that watching which
        // error comes back says nothing about which ids exist.
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task A_demand_is_accepted_rather_than_reported_as_done()
    {
        var account = await OpenAccountAsync();

        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        // 202, because what exists is a demand that has been accepted and not yet sent.
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.Equal("delete", body.GetProperty("requestType").GetString());

        // Derived from the company's catalog entry, never from the request.
        Assert.Equal("automated", body.GetProperty("strategy").GetString());

        Assert.Equal(0, body.GetProperty("attempt").GetInt32());
        Assert.Equal(account.ProfileId, body.GetProperty("profileId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("exposureId").ValueKind);
        Assert.Equal(_formBrokerId, body.GetProperty("broker").GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A demand needs no listing behind it.
    /// </summary>
    /// <remarks>
    /// The whole reason the schema was widened. An opt-out of sale is prospective, and a
    /// deletion request does not oblige somebody to prove the company holds their data.
    /// </remarks>
    [Fact]
    public async Task A_demand_can_cite_nothing_at_all()
    {
        var account = await OpenAccountAsync();

        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _mailBrokerId, requestType = "opt_out_sale" },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("exposureId").ValueKind);
        Assert.Equal("manual_email", body.GetProperty("strategy").GetString());
    }

    /// <summary>
    /// The deadline is snapshotted, and says which kind of promise it is.
    /// </summary>
    /// <remarks>
    /// No regime has been confirmed to reach these test companies, so every demand here
    /// gets the company's own target — which the response has to label as the courtesy it
    /// is, since a date alone is a number somebody reads as a guarantee.
    /// </remarks>
    [Fact]
    public async Task A_demand_carries_a_deadline_and_says_where_it_came_from()
    {
        var account = await OpenAccountAsync();

        var (_, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        Assert.Equal("operational_default", body.GetProperty("deadlineSource").GetString());

        var id = body.GetProperty("id").GetGuid();
        var (_, detail) = await _api.GetAsync($"{RemovalsPath}/{id}", account.Token);

        // Null is the real answer rather than a missing one: no confirmed statute reached
        // this company for this person, so the target is the company's own.
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("legalBasisId").ValueKind);

        var deadline = detail.GetProperty("deadlineAt").GetDateTimeOffset();
        Assert.True(deadline > DateTimeOffset.UtcNow, "The deadline is in the past.");
    }

    /// <summary>
    /// Post is refused rather than accepted and left stuck.
    /// </summary>
    [Fact]
    public async Task A_company_that_only_takes_paper_is_refused_at_the_door()
    {
        var account = await OpenAccountAsync();

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _postBrokerId, requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_demand_against_a_company_this_instance_never_heard_of_is_refused()
    {
        var account = await OpenAccountAsync();

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = Guid.NewGuid(), requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_demand_for_an_identity_this_account_does_not_manage_is_refused()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", profileId = theirs.ProfileId },
            mine.Token);

        // The same answer a profile that does not exist gets, because telling them apart
        // would confirm that an id belongs to another account.
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Only_one_live_demand_of_a_kind_per_company_and_identity()
    {
        var account = await OpenAccountAsync();
        var body = new { brokerId = _formBrokerId, requestType = "delete" };

        Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync(RemovalsPath, body, account.Token)).Status);

        var (second, _) = await _api.PostAsync(RemovalsPath, body, account.Token);

        Assert.Equal(HttpStatusCode.Conflict, second);
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// Different rights are different asks, and both may be open at once.
    /// </summary>
    /// <remarks>
    /// Some companies answer one and not the other, which is why the rule is keyed on the
    /// kind of demand rather than on the company alone.
    /// </remarks>
    [Fact]
    public async Task A_deletion_and_an_opt_out_can_both_be_open()
    {
        var account = await OpenAccountAsync();

        var (first, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var (second, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "opt_out_sale" },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(HttpStatusCode.Accepted, second);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task A_demand_can_cite_a_listing_it_is_about()
    {
        var account = await OpenAccountAsync();
        var listing = await SeedListingAsync(account, _formBrokerId);

        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", exposureId = listing },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(listing, body.GetProperty("exposureId").GetGuid());

        // The dismiss route reads this column to refuse a listing with a demand in flight
        // over it, so leaving it unset would make that refusal unreachable.
        Assert.Equal("requested", await ListingStatusAsync(listing));
    }

    /// <summary>
    /// Evidence has to be about what the demand is about.
    /// </summary>
    /// <remarks>
    /// Otherwise a company is sent one person's details as proof of another's listing —
    /// and because both identities belong to one account, the tenant boundary would not
    /// notice. The schema refuses it too; this gets there first and can say which half
    /// disagreed.
    /// </remarks>
    [Fact]
    public async Task A_demand_cannot_cite_a_listing_found_on_another_company()
    {
        var account = await OpenAccountAsync();
        var listing = await SeedListingAsync(account, _mailBrokerId);

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", exposureId = listing },
            account.Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_demand_cannot_cite_another_accounts_listing()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();
        var listing = await SeedListingAsync(theirs, _formBrokerId);

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", exposureId = listing },
            mine.Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /// <summary>
    /// Nothing is sent over a listing the person has said is not them.
    /// </summary>
    [Fact]
    public async Task A_dismissed_listing_cannot_be_demanded_over()
    {
        var account = await OpenAccountAsync();
        var listing = await SeedListingAsync(account, _formBrokerId, "dismissed");

        var (status, _) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", exposureId = listing },
            account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task An_account_sees_only_its_own_demands()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        await _api.PostAsync(RemovalsPath, new { brokerId = _formBrokerId, requestType = "delete" }, mine.Token);
        await _api.PostAsync(RemovalsPath, new { brokerId = _formBrokerId, requestType = "delete" }, theirs.Token);

        var (status, body) = await _api.GetAsync(RemovalsPath, mine.Token);

        Assert.Equal(HttpStatusCode.OK, status);

        var listed = Assert.Single(body.GetProperty("removalRequests").EnumerateArray());
        Assert.Equal(mine.ProfileId, listed.GetProperty("profileId").GetGuid());
    }

    [Fact]
    public async Task Another_accounts_demand_is_not_found_rather_than_forbidden()
    {
        var mine = await OpenAccountAsync();
        var theirs = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            theirs.Token);

        var id = opened.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"{RemovalsPath}/{id}", mine.Token)).Status);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _api.GetAsync($"{RemovalsPath}/{id}/timeline", mine.Token)).Status);

        var (cancel, _) = await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, mine.Token);
        Assert.Equal(HttpStatusCode.NotFound, cancel);
    }

    /// <summary>
    /// The list reads newest first, across more than one row.
    /// </summary>
    /// <remarks>
    /// Two rows because one cannot tell an ordered list from an unordered one, and the
    /// ordering here is applied across a join — where a sort of one input is not a sort of
    /// the result, and getting it wrong produces rows in whatever order the join emitted.
    /// </remarks>
    [Fact]
    public async Task Demands_are_listed_newest_first()
    {
        var account = await OpenAccountAsync();

        var (_, older) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var (_, newer) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _mailBrokerId, requestType = "delete" },
            account.Token);

        // Written directly, because two demands opened in the same test run land within the
        // same millisecond often enough that the assertion would pass on luck.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             UPDATE public.removal_request SET created_at = now() - interval '3 days'
                 WHERE id = '{older.GetProperty("id").GetGuid()}';
             UPDATE public.removal_request SET created_at = now() - interval '1 hour'
                 WHERE id = '{newer.GetProperty("id").GetGuid()}';
             """);

        var (_, body) = await _api.GetAsync(RemovalsPath, account.Token);
        var listed = body.GetProperty("removalRequests").EnumerateArray().ToArray();

        Assert.Equal(2, listed.Length);
        Assert.Equal(newer.GetProperty("id").GetGuid(), listed[0].GetProperty("id").GetGuid());
        Assert.Equal(older.GetProperty("id").GetGuid(), listed[1].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_list_can_be_narrowed_to_one_state()
    {
        var account = await OpenAccountAsync();

        await _api.PostAsync(RemovalsPath, new { brokerId = _formBrokerId, requestType = "delete" }, account.Token);

        var (_, queued) = await _api.GetAsync($"{RemovalsPath}?status=queued", account.Token);
        Assert.Single(queued.GetProperty("removalRequests").EnumerateArray());

        var (_, removed) = await _api.GetAsync($"{RemovalsPath}?status=removed", account.Token);
        Assert.Empty(removed.GetProperty("removalRequests").EnumerateArray());
    }

    [Fact]
    public async Task A_status_this_service_does_not_know_is_refused_rather_than_answered_emptily()
    {
        var account = await OpenAccountAsync();

        var (status, _) = await _api.GetAsync($"{RemovalsPath}?status=in_flight", account.Token);

        // An empty list here would say nothing has been demanded on somebody's behalf,
        // which is a sentence they would act on, produced from a typo.
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// The timeline is the attempts, and says so by carrying nothing else.
    /// </summary>
    /// <remarks>
    /// A demand nothing has attempted has an empty one, which is the honest answer: no
    /// table records that a request moved from queued to submitted, so there are no
    /// transitions to serve.
    /// </remarks>
    [Fact]
    public async Task A_demand_nothing_has_attempted_has_an_empty_timeline()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();
        var (status, body) = await _api.GetAsync($"{RemovalsPath}/{id}/timeline", account.Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.GetProperty("attempts").EnumerateArray());
        Assert.Equal(id, body.GetProperty("removalRequest").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_timeline_reads_forwards_through_the_attempts()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();

        // Written directly, the way a dispatcher will. Nothing creates jobs yet.
        await SeedAttemptAsync(account, id, 1, "failed");
        await SeedAttemptAsync(account, id, 2, "succeeded");

        var (_, body) = await _api.GetAsync($"{RemovalsPath}/{id}/timeline", account.Token);

        var attempts = body.GetProperty("attempts").EnumerateArray().ToArray();

        Assert.Equal(2, attempts.Length);
        Assert.Equal(1, attempts[0].GetProperty("attemptNumber").GetInt32());
        Assert.Equal(2, attempts[1].GetProperty("attemptNumber").GetInt32());
        Assert.Equal("failed", attempts[0].GetProperty("status").GetString());
        Assert.Equal("generic-web-form", attempts[0].GetProperty("connectorId").GetString());
    }

    [Fact]
    public async Task A_queued_demand_can_be_called_off()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();
        var (status, body) = await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, account.Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
    }

    /// <summary>
    /// Calling a demand off frees the listing it was about.
    /// </summary>
    /// <remarks>
    /// Otherwise the person is left unable to dismiss it, with a refusal naming a request
    /// they had just cancelled.
    /// </remarks>
    [Fact]
    public async Task Calling_off_a_demand_releases_the_listing_it_cited()
    {
        var account = await OpenAccountAsync();
        var listing = await SeedListingAsync(account, _formBrokerId);

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete", exposureId = listing },
            account.Token);

        Assert.Equal("requested", await ListingStatusAsync(listing));

        var id = opened.GetProperty("id").GetGuid();
        await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, account.Token);

        Assert.Equal("new", await ListingStatusAsync(listing));
    }

    /// <summary>
    /// A cancelled demand does not hold the slot against a later one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_demand_does_not_block_a_fresh_one()
    {
        var account = await OpenAccountAsync();
        var body = new { brokerId = _formBrokerId, requestType = "delete" };

        var (_, opened) = await _api.PostAsync(RemovalsPath, body, account.Token);
        var id = opened.GetProperty("id").GetGuid();

        await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, account.Token);

        Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync(RemovalsPath, body, account.Token)).Status);
    }

    /// <summary>
    /// A demand that has been answered cannot be unsent.
    /// </summary>
    /// <remarks>
    /// The lifecycle's rule rather than the endpoint's, and the refusal carries the
    /// lifecycle's own sentence — which is written to be read by whoever asked.
    /// </remarks>
    [Fact]
    public async Task A_demand_that_has_run_its_course_cannot_be_cancelled()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();
        await SetStatusAsync(id, "removed");

        var (status, _) = await _api.PostAsync($"{RemovalsPath}/{id}/cancel", new { }, account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("removed", await StatusAsync(id));
    }

    [Fact]
    public async Task A_failed_demand_can_be_sent_back_to_the_queue()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();
        await SetStatusAsync(id, "failed");

        var (status, body) = await _api.PostAsync($"{RemovalsPath}/{id}/retry", new { }, account.Token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("queued", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_demand_that_has_not_failed_cannot_be_retried()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();
        var (status, _) = await _api.PostAsync($"{RemovalsPath}/{id}/retry", new { }, account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("queued", await StatusAsync(id));
    }

    /// <summary>
    /// A demand that has used its attempts is refused rather than requeued.
    /// </summary>
    /// <remarks>
    /// The lifecycle allows failed to become queued and names a guard on it that only the
    /// caller can evaluate. This is that guard.
    /// </remarks>
    [Fact]
    public async Task A_demand_that_has_used_its_attempts_is_not_retried_again()
    {
        var account = await OpenAccountAsync();

        var (_, opened) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _formBrokerId, requestType = "delete" },
            account.Token);

        var id = opened.GetProperty("id").GetGuid();

        await SetStatusAsync(id, "failed");
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET attempt = 3 WHERE id = '{id}'");

        var (status, _) = await _api.PostAsync($"{RemovalsPath}/{id}/retry", new { }, account.Token);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("failed", await StatusAsync(id));
    }

    private async Task<Guid> IdOfAsync(string domain) =>
        await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{domain}'");

    private async Task<int> CountAsync() =>
        (int)await postgres.QueryAsOwnerAsync<long>("SELECT count(*) FROM public.removal_request");

    private async Task<string?> StatusAsync(Guid id) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.removal_request WHERE id = '{id}'");

    private async Task<string?> ListingStatusAsync(Guid id) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT status FROM public.exposure WHERE id = '{id}'");

    private async Task SetStatusAsync(Guid id, string status) =>
        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.removal_request SET status = '{status}' WHERE id = '{id}'");

    /// <summary>Writes one finding against the account's real scan, the way a worker will.</summary>
    private async Task<Guid> SeedListingAsync(Account account, Guid brokerId, string status = "new")
    {
        var id = Guid.NewGuid();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.exposure
                 (id, tenant_id, scan_id, privacy_profile_id, broker_id, status, confidence)
                 VALUES ('{id}', '{account.TenantId}', '{account.ScanId}', '{account.ProfileId}',
                         '{brokerId}', '{status}', 0.9);
             """);

        return id;
    }

    /// <summary>Writes one attempt against a demand, the way a dispatcher will.</summary>
    private async Task SeedAttemptAsync(Account account, Guid requestId, int number, string status) =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.removal_job
                 (tenant_id, removal_request_id, connector_id, status, attempt_number, run_at)
                 VALUES ('{account.TenantId}', '{requestId}', 'generic-web-form', '{status}',
                         {number}, now());
             """);

    private async Task<Account> OpenAccountAsync(bool removals = true)
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"rma-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        await _api.PostAsync(
            ConsentPath,
            new { scope = "scan", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        if (removals)
        {
            await _api.PostAsync(
                ConsentPath,
                new { scope = "auto_removal", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
                token);
        }

        // A real scan, so a seeded listing hangs off one the way the composite keys require.
        var (_, scan) = await _api.PostAsync(ScansPath, new { }, token);

        return new Account(
            token,
            ApiClient.TenantId(session),
            scan.GetProperty("id").GetGuid(),
            scan.GetProperty("profileId").GetGuid());
    }

    private sealed record Account(string Token, Guid TenantId, Guid ScanId, Guid ProfileId);
}
