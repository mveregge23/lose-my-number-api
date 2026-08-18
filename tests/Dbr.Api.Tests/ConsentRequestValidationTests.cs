// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Domain.Consent;

namespace Dbr.Api.Tests;

/// <summary>
/// What the consent route accepts, and what it will not guess at.
/// </summary>
public class ConsentRequestValidationTests
{
    [Fact]
    public void A_scope_is_spelled_the_way_the_column_stores_it()
    {
        Assert.Equal(ConsentScope.Scan, ConsentRequestValidation.ParseScope("scan"));
        Assert.Equal(ConsentScope.AutoRemoval, ConsentRequestValidation.ParseScope("auto_removal"));
        Assert.Equal(ConsentScope.AutoResubmit, ConsentRequestValidation.ParseScope("auto_resubmit"));
    }

    [Theory]
    [InlineData("AutoRemoval")]
    [InlineData("auto removal")]
    [InlineData("autoRemoval")]
    [InlineData("AUTO_REMOVAL")]
    public void A_scope_spelled_any_other_way_is_not_one(string scope)
    {
        // One spelling, matched exactly. A tolerant parse here would accept names the
        // check constraint behind the column rejects, turning a clear 400 into a 500 —
        // and would leave the API and the database disagreeing about what a scope is
        // called.
        Assert.Null(ConsentRequestValidation.ParseScope(scope));
    }

    [Fact]
    public void Every_scope_has_a_spelling_a_client_can_send_back()
    {
        // The round trip is what matters: a client reads a scope out of a response and
        // posts it back, so a name that goes out and does not come back in is a switch
        // nobody can flip.
        foreach (var scope in Enum.GetValues<ConsentScope>())
        {
            Assert.Equal(scope, ConsentRequestValidation.ParseScope(ConsentRequestValidation.ToWire(scope)));
        }
    }

    [Fact]
    public void An_unknown_scope_is_refused_with_the_three_that_exist()
    {
        var problem = ConsentRequestValidation.Validate(new RecordConsentRequest("scanning", true, "v1"));

        Assert.NotNull(problem);
        Assert.Contains("auto_resubmit", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_that_does_not_say_which_way_is_refused()
    {
        // Rather than defaulting. A missing field would default to withdrawing, and a
        // client that forgot to send it would silently take a permission away.
        var problem = ConsentRequestValidation.Validate(new RecordConsentRequest("scan", null, "v1"));

        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_directions_are_a_decision(bool granted)
    {
        Assert.Null(ConsentRequestValidation.Validate(new RecordConsentRequest("scan", granted, "v1")));
    }

    [Fact]
    public void The_policy_version_is_not_checked_here()
    {
        // Deliberately: what it is compared against is a setting this layer has no
        // business reading, and a blank one is refused where the comparison happens
        // rather than being turned into a different answer here.
        Assert.Null(ConsentRequestValidation.Validate(new RecordConsentRequest("scan", true, null)));
    }
}
