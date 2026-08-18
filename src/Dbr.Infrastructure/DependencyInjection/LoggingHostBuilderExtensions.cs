// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// The single definition of how a Dbr process writes a log line.
/// </summary>
/// <remarks>
/// Separate from the rest of the registrations and applied by both hosts, for the same
/// reason <c>UseDbr()</c> owns the database connection: a process that assembled its own
/// logging pipeline could assemble one without the redaction step, and the call site
/// that did it would look like ordinary setup.
/// </remarks>
public static class LoggingHostBuilderExtensions
{
    /// <summary>Human-readable, for a terminal somebody is watching.</summary>
    private const string DeveloperTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Replaces the default logging pipeline with Serilog, redaction attached.
    /// </summary>
    /// <remarks>
    /// Reads the <c>Serilog</c> configuration section, so levels stay an operator's to
    /// set without a rebuild.
    /// </remarks>
    public static IHostApplicationBuilder AddDbrLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The console and debug providers the host attaches by default write an event as
        // it was authored, with no redaction anywhere in the path. Registering Serilog
        // below replaces the logger factory outright, which already leaves them holding
        // nothing — so this is not what makes the redaction work today, and the honest
        // reason to keep it is narrower: it means no provider is left registered for a
        // later change to start handing events to. Swapping Serilog's registration for
        // one that adds a provider instead of replacing the factory is a plausible
        // future edit, and it is the edit that would quietly reactivate every one of
        // them.
        builder.Logging.ClearProviders();

        var formatter = Formatter(builder.Environment);

        builder.Services.AddSerilog(configuration => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .Redacted()
            .WriteTo.Console(new RedactingTextFormatter(formatter)));

        return builder;
    }

    /// <summary>
    /// The redaction steps, as one thing that can be applied and tested.
    /// </summary>
    /// <remarks>
    /// Extracted so that the tests exercise this pipeline rather than a second one
    /// assembled to look like it. A test that rebuilds the configuration by hand keeps
    /// passing after somebody deletes a step from the real one, which is the failure it
    /// was written to catch.
    /// </remarks>
    internal static LoggerConfiguration Redacted(this LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            // Before the enricher, and doing a different job: this stops an identity
            // being taken apart, the enricher cleans up what other call sites named.
            .Destructure.With(new PiiDestructuringPolicy())

            // Last on purpose. Enrichers run in order and only the final one sees
            // everything the others added, so anything registered after this could put
            // an address back into an event that has already been cleaned.
            .Enrich.With(new PiiRedactingEnricher());
    }

    /// <summary>
    /// JSON everywhere except a developer's own terminal.
    /// </summary>
    /// <remarks>
    /// Compose and anything past it read this with a machine, and the properties are the
    /// part worth keeping once a line has been redacted — a rendered sentence with
    /// <c>[redacted]</c> in the middle of it is far less useful than the same event with
    /// its ids intact and one field withheld.
    /// </remarks>
    private static ITextFormatter Formatter(IHostEnvironment environment) =>
        environment.IsDevelopment()
            ? new MessageTemplateTextFormatter(DeveloperTemplate, formatProvider: null)
            : new CompactJsonFormatter();
}
