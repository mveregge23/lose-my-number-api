// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Mail;
using Dbr.Infrastructure.Removals;
using Dbr.Integration.Tests.Fixtures;
using Dbr.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dbr.Integration.Tests;

/// <summary>
/// A company answers, and the answer reaches the attempt it answers.
/// </summary>
/// <remarks>
/// <para>
/// These claims need a real database and cannot be made anywhere else: that the role which
/// resolves a reply can see attempts that sent something and nothing else, that a reply
/// resolves by the id an attempt stored, that the same message read twice is filed once,
/// and that what was filed reaches the route a person reads.
/// </para>
/// <para>
/// The source is a double and the mail server is not: what a real IMAP conversation does is
/// not what any of this is about. What arrives is a message, and the question is what
/// happens to it.
/// </para>
/// </remarks>
[Collection(ProfileVaultCollection.Name)]
public class MailIngestTests(PostgresFixture postgres, OpenBaoFixture openBao) : IAsyncLifetime
{
    private const string RemovalsPath = "/api/v1/removal-requests";

    private const string Domain = "answers.test";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly List<TestAuthenticator> _authenticators = [];

    private readonly StubInboundMailSource _arriving = new();

    private DbrApiFactory _factory = null!;

    private HttpClient _client = null!;

    private ApiClient _api = null!;

    private ServiceProvider _worker = null!;

    private Guid _brokerId;

    private string BrokerDomain => $"ing-broker-{_suffix}.test";

    public async ValueTask InitializeAsync()
    {
        _factory = new DbrApiFactory(postgres.ConnectionString, openBao.Address, openBao.Token);
        _client = _factory.CreateClient();
        _api = new ApiClient(_client);

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker (name, domain, removal_method, sla_days, active)
                 VALUES ('ING Broker {_suffix}', '{BrokerDomain}', 'email', 30, true);
             """);

        _brokerId = await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{BrokerDomain}'");

        _worker = BuildWorker();
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();

        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var authenticator in _authenticators)
        {
            authenticator.Dispose();
        }

        await postgres.ExecuteAsOwnerAsync(
            $"""
             DELETE FROM public.broker_reply;
             DELETE FROM public.removal_job;
             DELETE FROM public.removal_request;
             DELETE FROM public.consent_record;
             DELETE FROM vault.profile_identity;
             DELETE FROM public.privacy_profile;
             DELETE FROM public.tenant;
             DELETE FROM public.passkey_ceremony;
             DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';
             """);
    }

    /// <summary>
    /// A reply quoting the id a demand went out under reaches that attempt.
    /// </summary>
    /// <remarks>
    /// The path that matters most, because it is the one the first real demand had to use:
    /// the relay rewrote the per-attempt return address on the way out, so the reply came
    /// back naming no attempt at all and the thread was the only thing tying it to one.
    /// </remarks>
    [Fact]
    public async Task A_reply_quoting_a_demand_is_filed_against_the_attempt_that_sent_it()
    {
        var account = await OpenAccountAsync();
        var (requestId, jobId) = await SentDemandAsync(account, "demand-1@relay.test");

        _arriving.Add(Message("1-1", inReplyTo: "demand-1@relay.test"));

        Assert.Equal(1, await SweepAsync());

        var filed = await FiledAsync(jobId);
        Assert.Equal("thread_headers", filed);

        var timeline = await TimelineAsync(account, requestId);
        var reply = Assert.Single(timeline);
        Assert.Equal(jobId, reply.GetProperty("attemptId").GetGuid());
        Assert.Equal("privacy@company.test", reply.GetProperty("from").GetString());
    }

    /// <summary>
    /// A reply to an attempt's own address is filed as having been resolved that way.
    /// </summary>
    /// <remarks>
    /// The column is not bookkeeping. An instance whose replies all resolve by thread
    /// headers is one whose return addresses are being rewritten by the relay it sends
    /// through, and this is the only place that becomes visible — nothing on the sending
    /// side can see it happen.
    /// </remarks>
    [Fact]
    public async Task A_reply_to_an_attempts_own_address_says_so()
    {
        var account = await OpenAccountAsync();
        var (_, jobId) = await SentDemandAsync(account, "demand-2@relay.test");

        _arriving.Add(Message("1-2", to: [JobMailbox.For(jobId, Domain).Address]));

        Assert.Equal(1, await SweepAsync());
        Assert.Equal("mailbox_address", await FiledAsync(jobId));
    }

    /// <summary>
    /// The same message offered twice is filed once.
    /// </summary>
    /// <remarks>
    /// Which is what a source doing the right thing produces: a message is offered until it
    /// is acknowledged, so a process that files a reply and stops before acknowledging will
    /// be offered it again. Filing it twice would make one answer look like two.
    /// </remarks>
    [Fact]
    public async Task A_message_read_twice_is_one_answer()
    {
        var account = await OpenAccountAsync();
        var (requestId, _) = await SentDemandAsync(account, "demand-3@relay.test");

        _arriving.Add(Message("1-3", inReplyTo: "demand-3@relay.test"));
        Assert.Equal(1, await SweepAsync());

        _arriving.Unacknowledge();
        Assert.Equal(0, await SweepAsync());

        Assert.Single(await TimelineAsync(account, requestId));
    }

    /// <summary>
    /// Mail that answers nothing is read, acknowledged, and filed nowhere.
    /// </summary>
    /// <remarks>
    /// The ordinary case for any mailbox that demands go out from. Leaving it unread would
    /// mean every pass forever re-reading the same accumulating pile to reach whatever is
    /// new behind it.
    /// </remarks>
    [Fact]
    public async Task Mail_that_answers_no_demand_is_not_filed_and_is_not_read_again()
    {
        await OpenAccountAsync();

        _arriving.Add(Message("1-4", inReplyTo: "someone-elses@relay.test"));

        Assert.Equal(0, await SweepAsync());
        Assert.Empty(_arriving.Pending);
        Assert.Equal(0, await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM public.broker_reply"));
    }

    /// <summary>
    /// An attempt that sent nothing is invisible to the role that resolves replies.
    /// </summary>
    /// <remarks>
    /// The grant is the narrowest of the four this role holds, and this is what makes it
    /// narrow: an attempt nobody could have answered — a web form, or a demand that never
    /// left — cannot be reached through it at all, so the privilege cannot be turned into a
    /// way of watching what an account is doing.
    /// </remarks>
    [Fact]
    public async Task An_attempt_that_sent_nothing_cannot_be_resolved_to()
    {
        var account = await OpenAccountAsync();
        var (_, jobId) = await SentDemandAsync(account, sentMessageId: null);

        var directory = new AnsweredDemandDirectory(postgres.ConnectionString);

        var match = await directory.ResolveAsync(
            new ReplyEvidence(jobId, []),
            TestContext.Current.CancellationToken);

        Assert.Null(match);
    }

    private async Task<int> SweepAsync() =>
        await _worker.GetRequiredService<MailIngestService>()
            .SweepAsync(TestContext.Current.CancellationToken);

    private async Task<string?> FiledAsync(Guid jobId) =>
        await postgres.QueryAsOwnerAsync<string>(
            $"SELECT matched_by FROM public.broker_reply WHERE removal_job_id = '{jobId}'");

    private async Task<IReadOnlyList<System.Text.Json.JsonElement>> TimelineAsync(
        Account account,
        Guid requestId)
    {
        var (status, body) = await _api.GetAsync($"{RemovalsPath}/{requestId}/timeline", account.Token);

        Assert.Equal(HttpStatusCode.OK, status);

        return body.GetProperty("replies").EnumerateArray().ToList();
    }

    /// <summary>
    /// An account with a demand that has been attempted once, by mail.
    /// </summary>
    /// <remarks>
    /// The attempt is written directly rather than dispatched. What is being asserted
    /// starts at the moment a company answers, and going through the dispatcher to reach
    /// it would make every one of these tests also a test of the dispatcher.
    /// </remarks>
    private async Task<(Guid RequestId, Guid JobId)> SentDemandAsync(
        Account account,
        string? sentMessageId)
    {
        var (status, body) = await _api.PostAsync(
            RemovalsPath,
            new { brokerId = _brokerId, requestType = "delete" },
            account.Token);

        Assert.Equal(HttpStatusCode.Accepted, status);

        var requestId = body.GetProperty("id").GetGuid();
        var jobId = Guid.NewGuid();
        var sent = sentMessageId is null ? "NULL" : $"'{sentMessageId}'";

        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.removal_job
                 (id, tenant_id, removal_request_id, connector_id, status, attempt_number,
                  run_at, sent_message_id)
                 VALUES ('{jobId}', '{account.TenantId}', '{requestId}', 'templated-email',
                         'succeeded', 1, now(), {sent});
             """);

        return (requestId, jobId);
    }

    private async Task<Account> OpenAccountAsync()
    {
        var authenticator = new TestAuthenticator();
        _authenticators.Add(authenticator);

        var session = await _api.SignUpAsync($"ingest-{Guid.NewGuid():N}@example.test", authenticator);
        var token = ApiClient.AccessToken(session);

        // A demand cannot be opened without permission to make one, which is the check
        // doing its job rather than something to work around.
        var (granted, _) = await _api.PostAsync(
            "/api/v1/profile/consent",
            new { scope = "auto_removal", granted = true, policyVersion = DbrApiFactory.ConsentPolicyVersion },
            token);

        Assert.Equal(HttpStatusCode.OK, granted);

        return new Account(token, ApiClient.TenantId(session));
    }

    private static InboundMessage Message(
        string sourceRef,
        IReadOnlyList<string>? to = null,
        string? inReplyTo = null) =>
        new()
        {
            SourceRef = sourceRef,
            From = "privacy@company.test",
            To = to ?? [$"reader@{Domain}"],
            MessageId = $"{sourceRef}@company.test",
            InReplyTo = inReplyTo,
            Subject = "Re: Request to delete personal information",
            ReceivedAt = DateTimeOffset.UtcNow,
        };

    private ServiceProvider BuildWorker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Core"] = postgres.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbrPersistence(configuration);

        services.AddSingleton(Options.Create(new MailOptions { Domain = Domain }));
        services.AddSingleton<IJobMailboxes, JobMailboxes>();
        services.AddSingleton<IInboundMailSource>(_arriving);
        services.AddSingleton<IAnsweredDemandDirectory>(
            new AnsweredDemandDirectory(postgres.ConnectionString));
        services.AddSingleton(new InboundMailOptions { Enabled = true });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IReplyFiler, ReplyFiler>();
        services.AddSingleton<MailIngestService>();

        return services.BuildServiceProvider();
    }

    private sealed record Account(string Token, Guid TenantId);

    /// <summary>
    /// A mailbox with whatever a test put in it.
    /// </summary>
    /// <remarks>
    /// Keeps offering a message until it is acknowledged, which is the half of the port's
    /// contract the filing has to tolerate — and <see cref="Unacknowledge"/> is how a test
    /// plays the process that filed a reply and stopped before saying so.
    /// </remarks>
    private sealed class StubInboundMailSource : IInboundMailSource
    {
        private readonly List<InboundMessage> _messages = [];

        private readonly List<InboundMessage> _acknowledged = [];

        public IReadOnlyList<InboundMessage> Pending =>
            _messages.Except(_acknowledged).ToList();

        public void Add(InboundMessage message) => _messages.Add(message);

        public void Unacknowledge() => _acknowledged.Clear();

        public Task<IReadOnlyList<InboundMessage>> FetchAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InboundMessage>>(Pending.Take(limit).ToList());

        public Task AcknowledgeAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            _acknowledged.Add(message);
            return Task.CompletedTask;
        }
    }
}
