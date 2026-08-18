// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Dbr.Infrastructure.Tests.Observability;

/// <summary>
/// What survives a log call and what does not.
/// </summary>
/// <remarks>
/// Written against a real Serilog pipeline rather than by calling the enricher directly,
/// because the thing worth checking is what a sink receives — an enricher that rewrote
/// the right property but left the rendered message intact would pass a direct test and
/// leak in production.
/// </remarks>
public class PiiRedactingEnricherTests
{
    [Fact]
    public void A_property_named_after_an_identity_field_is_withheld()
    {
        var (logger, events) = Capture();

        logger.Information("Replacing profile {ProfileId} for {Email}", Guid.Empty, "alex@example.test");

        var written = events.Single();

        // The id stays. That is the whole point of the rule — an event still has to be
        // able to say which profile it was about.
        Assert.Equal(Guid.Empty, Scalar(written, "ProfileId"));
        Assert.Equal(PiiRedaction.Marker, Scalar(written, "Email"));

        // And the rendered line, which is what a plain-text sink writes.
        Assert.DoesNotContain("alex@example.test", written.RenderMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("TenantEmail")]
    [InlineData("Name")]
    [InlineData("Names")]
    [InlineData("Line1")]
    [InlineData("PostalCode")]
    [InlineData("DateOfBirth")]
    public void Every_name_on_the_list_is_withheld_however_it_is_qualified(string property)
    {
        var (logger, events) = Capture();

        logger.Information("{" + property + "}", "Alex Whitfield");

        Assert.Equal(PiiRedaction.Marker, Scalar(events.Single(), property));
    }

    [Theory]
    [InlineData("EmailVerified")]
    [InlineData("NameCount")]
    [InlineData("AddressId")]
    public void A_name_that_merely_contains_one_is_left_alone(string property)
    {
        // The redactor has to stay worth having. If it eats counts and ids because they
        // share a word with a field, people stop naming things accurately to get around
        // it, and then the names it does depend on stop being trustworthy.
        var (logger, events) = Capture();

        logger.Information("{" + property + "}", "keep me");

        Assert.Equal("keep me", Scalar(events.Single(), property));
    }

    [Fact]
    public void An_address_shaped_value_is_withheld_whatever_it_was_called()
    {
        // The net for the case the names cannot cover: a value that reached a log call
        // through a property nobody would think to add to a list.
        var (logger, events) = Capture();

        logger.Information("Broker replied: {Detail}", "no match for alex@example.test");

        Assert.Equal(PiiRedaction.Marker, Scalar(events.Single(), "Detail"));
    }

    [Fact]
    public void An_identity_is_withheld_whole_rather_than_field_by_field()
    {
        // Destructured, so Serilog would otherwise unpack every member of it.
        var (logger, events) = Capture();

        logger.Information("{@Anything}", Fields());

        Assert.Equal(PiiRedaction.Marker, Scalar(events.Single(), "Anything"));
    }

    [Fact]
    public void An_identity_logged_without_destructuring_is_still_withheld()
    {
        // The nastier half of the same mistake, and the one the enricher cannot fix.
        // Without the @, Serilog never consults a destructuring policy — it stringifies
        // the value on the way in, so by the time anything here runs the address has
        // already been turned into text. What stops it is the type's own ToString, which
        // is why that override exists rather than being left to the pipeline.
        var (logger, events) = Capture();

        logger.Information(
            "{Anything}",
            new ProfileAddress(Guid.Empty, "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US"));

        var rendered = events.Single().RenderMessage();

        Assert.DoesNotContain("Rowan Lane", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Sacramento", rendered, StringComparison.Ordinal);
        Assert.Contains("withheld", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_nested_inside_something_else_is_judged_by_its_own_name()
    {
        var (logger, events) = Capture();

        logger.Information("{@Request}", new { ProfileId = Guid.Empty, Address = "12 Rowan Lane" });

        var structure = Assert.IsType<StructureValue>(events.Single().Properties["Request"]);
        var members = structure.Properties.ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal(Guid.Empty, ((ScalarValue)members["ProfileId"]).Value);
        Assert.Equal(PiiRedaction.Marker, ((ScalarValue)members["Address"]).Value);
    }

    [Fact]
    public void A_list_of_them_does_not_get_through_by_being_a_list()
    {
        var (logger, events) = Capture();

        logger.Information("{@Results}", new[] { "nobody@example.test", "someone@example.test" });

        var sequence = Assert.IsType<SequenceValue>(events.Single().Properties["Results"]);

        Assert.All(
            sequence.Elements,
            element => Assert.Equal(PiiRedaction.Marker, ((ScalarValue)element).Value));
    }

    [Fact]
    public void A_dictionary_key_is_a_name_like_any_other()
    {
        var (logger, events) = Capture();

        logger.Information(
            "{@Released}",
            new Dictionary<string, string> { ["Email"] = "alex@example.test", ["BrokerId"] = "b-1" });

        var dictionary = Assert.IsType<DictionaryValue>(events.Single().Properties["Released"]);
        var entries = dictionary.Elements.ToDictionary(e => (string)e.Key.Value!, e => e.Value);

        Assert.Equal(PiiRedaction.Marker, ((ScalarValue)entries["Email"]).Value);
        Assert.Equal("b-1", ((ScalarValue)entries["BrokerId"]).Value);
    }

    [Fact]
    public void The_frameworks_own_words_keep_their_own_meanings()
    {
        // Found by running it rather than by thinking about it: the API came up
        // announcing that it was listening on [redacted] in the [redacted] environment,
        // because ASP.NET Core calls a listening URL {address} and an environment
        // {envName}. Both are its vocabulary, not this codebase's.
        var (logger, events) = Capture();
        var framework = logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.Hosting.Lifetime");

        framework.Information("Now listening on: {address}", "http://[::]:8080");
        framework.Information("Hosting environment: {envName}", "Production");

        Assert.Equal("http://[::]:8080", Scalar(events[0], "address"));
        Assert.Equal("Production", Scalar(events[1], "envName"));
    }

    [Fact]
    public void A_value_that_is_identifying_on_its_own_is_withheld_from_anybody()
    {
        // The names are scoped to our own call sites; the value and type rules are not.
        // A framework event quoting an address is still an event quoting an address.
        var (logger, events) = Capture();

        logger
            .ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Routing")
            .Information("Matched route {Route}", "/users/alex@example.test/profile");

        Assert.Equal(PiiRedaction.Marker, Scalar(events.Single(), "Route"));
    }

    [Fact]
    public void Our_own_call_sites_still_get_the_whole_list()
    {
        var (logger, events) = Capture();

        logger
            .ForContext(Constants.SourceContextPropertyName, "Dbr.Infrastructure.Vault.ProfileService")
            .Information("Storing {Address}", "12 Rowan Lane");

        Assert.Equal(PiiRedaction.Marker, Scalar(events.Single(), "Address"));
    }

    [Fact]
    public void Everything_a_log_line_is_for_survives_it()
    {
        // The failure that would make this whole mechanism unusable is over-reach, and
        // it would show up as an event nobody can act on rather than as a test failing
        // somewhere obvious.
        var (logger, events) = Capture();

        logger.Information(
            "Removal {RemovalRequestId} for broker {BrokerId} moved to {Status} on attempt {Attempt}",
            Guid.Empty,
            "acme-data",
            ProfileRelationship.Self,
            3);

        var written = events.Single();

        Assert.Equal(Guid.Empty, Scalar(written, "RemovalRequestId"));
        Assert.Equal("acme-data", Scalar(written, "BrokerId"));
        Assert.Equal(3, Scalar(written, "Attempt"));
        Assert.DoesNotContain(PiiRedaction.Marker, written.RenderMessage(), StringComparison.Ordinal);
    }

    private static ProfileIdentityFields Fields() =>
        new(
            ["Alex Whitfield"],
            [new ProfileAddress(Guid.Empty, "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US")],
            [new ProfileContact(Guid.Empty, ProfileContactKind.Email, "alex@example.test")],
            new DateOnly(1985, 4, 17));

    private static object? Scalar(LogEvent written, string property) =>
        ((ScalarValue)written.Properties[property]).Value;

    /// <summary>
    /// The redaction <c>AddDbrLogging</c> applies — the same call, not a copy of it —
    /// writing to a list instead of a console.
    /// </summary>
    private static (ILogger Logger, List<LogEvent> Events) Capture()
    {
        var events = new List<LogEvent>();

        var logger = new LoggerConfiguration()
            .Redacted()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();

        return (logger, events);
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
