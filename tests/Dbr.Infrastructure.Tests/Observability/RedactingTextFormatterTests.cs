// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Observability;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace Dbr.Infrastructure.Tests.Observability;

/// <summary>
/// The last thing between a formatted line and a sink.
/// </summary>
/// <remarks>
/// This layer exists for exactly one thing the enricher cannot reach: an exception. Its
/// message is not a property, so nothing upstream can rewrite it — and the case is not
/// hypothetical, because a duplicate address at signup arrives as a database error that
/// quotes the value that collided.
/// </remarks>
public class RedactingTextFormatterTests
{
    [Fact]
    public void An_exception_that_quotes_an_address_does_not_reach_the_sink_with_it()
    {
        var written = Format(new InvalidOperationException(
            "duplicate key value violates unique constraint: Key (email)=(alex@example.test) already exists."));

        Assert.DoesNotContain("alex@example.test", written, StringComparison.Ordinal);
        Assert.Contains(PiiRedaction.Marker, written, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_exception_was_still_reaches_it()
    {
        // Redaction that took the diagnosis with the address would be traded for
        // nothing — the reason to keep the exception at all is to know what failed.
        var written = Format(new InvalidOperationException("duplicate key for alex@example.test"));

        Assert.Contains("InvalidOperationException", written, StringComparison.Ordinal);
        Assert.Contains("duplicate key", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_line_comes_through_unchanged()
    {
        var written = Format(exception: null);

        Assert.Contains("acme-data", written, StringComparison.Ordinal);
        Assert.DoesNotContain(PiiRedaction.Marker, written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_is_still_the_json_the_inner_formatter_produced()
    {
        // The marker carries no quote and no backslash on purpose, so substituting it
        // into an already-encoded document cannot break the encoding.
        var written = Format(new InvalidOperationException("alex@example.test"));

        Assert.Equal('{', written.TrimStart()[0]);

        using var parsed = System.Text.Json.JsonDocument.Parse(written);
        Assert.True(parsed.RootElement.TryGetProperty("@x", out _));
    }

    private static string Format(Exception? exception)
    {
        var template = new MessageTemplateParser().Parse("Broker {BrokerId} answered");

        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Error,
            exception,
            template,
            [new LogEventProperty("BrokerId", new ScalarValue("acme-data"))]);

        var output = new StringWriter();
        new RedactingTextFormatter(new CompactJsonFormatter()).Format(logEvent, output);

        return output.ToString();
    }
}
