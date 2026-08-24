// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;

namespace Dbr.Api.Tests;

/// <summary>
/// What the scan request refuses before anything reaches a database.
/// </summary>
public class ScanRequestValidationTests
{
    [Fact]
    public void An_empty_request_is_the_ordinary_one()
    {
        // Both fields omitted means "my own identity, the whole catalog", which is the
        // common case and must not need saying.
        Assert.Null(ScanRequestValidation.Validate(new RequestScanRequest(null, null)));
    }

    [Fact]
    public void Naming_a_profile_and_brokers_is_fine()
    {
        var request = new RequestScanRequest(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Null(ScanRequestValidation.Validate(request));
    }

    [Fact]
    public void An_empty_broker_list_is_not_an_error()
    {
        // Sent by a client that built a list and added nothing to it. It means the same
        // as leaving the field out, and refusing it would be refusing a request that says
        // exactly what the omitted form says.
        Assert.Null(ScanRequestValidation.Validate(new RequestScanRequest(null, [])));
    }

    [Fact]
    public void More_brokers_than_the_ceiling_is_refused()
    {
        var tooMany = Enumerable.Range(0, ScanRequestValidation.MaxBrokerIds + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var problem = ScanRequestValidation.Validate(new RequestScanRequest(null, tooMany));

        Assert.NotNull(problem);
        Assert.Contains("whole catalog", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_the_ceiling_is_allowed()
    {
        // The bound is a limit, not an off-by-one waiting to refuse a legitimate request
        // at the exact size it documents.
        var atLimit = Enumerable.Range(0, ScanRequestValidation.MaxBrokerIds)
            .Select(_ => Guid.NewGuid())
            .ToList();

        Assert.Null(ScanRequestValidation.Validate(new RequestScanRequest(null, atLimit)));
    }

    [Fact]
    public void An_all_zero_broker_id_is_named_rather_than_reported_as_unknown()
    {
        // What a client sends when it meant to send nothing. Saying so beats a round trip
        // to the catalog that comes back "no broker with id 00000000-...".
        var problem = ScanRequestValidation.Validate(
            new RequestScanRequest(null, [Guid.NewGuid(), Guid.Empty]));

        Assert.NotNull(problem);
        Assert.Contains("broker id cannot be empty", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_all_zero_profile_id_is_named_rather_than_looked_up()
    {
        var problem = ScanRequestValidation.Validate(new RequestScanRequest(Guid.Empty, null));

        Assert.NotNull(problem);
        Assert.Contains("your own identity", problem, StringComparison.Ordinal);
    }

    // The structural guardrail moved to ScanGuardrailTests, which asserts it by the type
    // of each property rather than by their names. Naming them made adding a second
    // identifier — a perfectly legitimate change — look like a guardrail breach, which is
    // how a check earns a reputation for crying wolf.
}
