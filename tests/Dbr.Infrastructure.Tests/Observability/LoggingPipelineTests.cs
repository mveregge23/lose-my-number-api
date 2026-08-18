// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dbr.Infrastructure.Tests.Observability;

/// <summary>
/// The pipeline a composition root actually gets, asserted against what it writes.
/// </summary>
/// <remarks>
/// <para>
/// Not a second <c>LoggerConfiguration</c> built to match the real one — a real host,
/// configured by the same call <c>Program.cs</c> makes, with the console captured. What
/// this can prove and a unit test cannot is that nothing else is still attached: a
/// default provider left in place would write the same event a second time, unredacted,
/// and every test of the enricher would still pass.
/// </para>
/// <para>
/// The class is a collection of its own because redirecting the console is process-wide.
/// </para>
/// </remarks>
[Collection(nameof(LoggingPipelineTests))]
public class LoggingPipelineTests
{
    [Fact]
    public void Nothing_writes_a_line_that_skipped_the_redaction()
    {
        var written = CaptureConsole(logger =>
            logger.LogInformation("Signup failed for {Email}", "alex@example.test"));

        Assert.DoesNotContain("alex@example.test", written, StringComparison.Ordinal);
        Assert.Contains(PiiRedaction.Marker, written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_is_withheld_by_the_assembled_pipeline_and_not_only_by_the_last_net()
    {
        // Deliberately a name rather than an address. An address is caught twice over —
        // by the enricher and again by the formatter, which recognises the shape — so a
        // test using one keeps passing when the enricher is missing from the pipeline
        // entirely. A name has only the enricher between it and the sink, which is what
        // makes this the test that notices.
        var written = CaptureConsole(logger =>
            logger.LogInformation("Replacing the profile of {Name}", "Alex Whitfield"));

        Assert.DoesNotContain("Alex Whitfield", written, StringComparison.Ordinal);
        Assert.Contains(PiiRedaction.Marker, written, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_else_is_left_attached_to_write_the_same_event_again()
    {
        // Two things, and neither shows up in what gets printed. Serilog owning the
        // factory is what makes every log call in the process go through the redaction;
        // no provider being left registered is what stops a later change to how Serilog
        // is attached from quietly reactivating the console and debug providers the host
        // adds by default, which write an event exactly as it was authored.
        //
        // Structural rather than captured output for a practical reason as well: the
        // default console provider writes from a background thread, so its absence
        // cannot be proven by reading stdout without waiting on a flush that may never
        // come.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        builder.AddDbrLogging();

        using var host = builder.Build();

        Assert.StartsWith(
            "Serilog.",
            host.Services.GetRequiredService<ILoggerFactory>().GetType().FullName!,
            StringComparison.Ordinal);

        Assert.Empty(host.Services.GetServices<ILoggerProvider>());
    }

    [Fact]
    public void An_exception_goes_through_it_too()
    {
        var written = CaptureConsole(logger => logger.LogError(
            new InvalidOperationException("Key (email)=(alex@example.test) already exists."),
            "Could not open the account"));

        Assert.DoesNotContain("alex@example.test", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_development_a_line_is_one_json_object()
    {
        // What compose collects and what the OTLP sink will ship later. A rendered
        // sentence would lose the properties that survived redaction, which are the
        // part worth keeping.
        var written = CaptureConsole(
            logger => logger.LogInformation("Broker {BrokerId} answered", "acme-data"),
            environment: "Production");

        using var parsed = System.Text.Json.JsonDocument.Parse(written.Trim());

        Assert.Equal("acme-data", parsed.RootElement.GetProperty("BrokerId").GetString());
    }

    [Fact]
    public void An_operator_still_sets_the_level_without_a_rebuild()
    {
        // It was theirs to set before Serilog arrived, and taking it away in exchange
        // for redaction would be a trade nobody asked for.
        var written = CaptureConsole(
            logger => logger.LogInformation("Broker {BrokerId} answered", "acme-data"),
            settings: new Dictionary<string, string?> { ["Serilog:MinimumLevel:Default"] = "Warning" });

        Assert.DoesNotContain("acme-data", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a host the way the composition roots do and returns whatever it printed.
    /// </summary>
    private static string CaptureConsole(
        Action<ILogger<LoggingPipelineTests>> log,
        string environment = "Production",
        IDictionary<string, string?>? settings = null)
    {
        var captured = new StringWriter();
        var original = Console.Out;

        // Redirected before the host is built: the console sink takes its writer when it
        // is constructed, so a redirect afterwards would capture nothing and the test
        // would pass by finding no address in an empty string.
        Console.SetOut(captured);

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = environment,
            });

            builder.Configuration.AddInMemoryCollection(
                settings ?? new Dictionary<string, string?>());

            builder.AddDbrLogging();

            using var host = builder.Build();
            log(host.Services.GetRequiredService<ILogger<LoggingPipelineTests>>());
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString();
    }
}
