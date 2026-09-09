// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Mail;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>What the sender refuses to start without.</summary>
public class MailOptionsTests
{
    private static MailOptions Configured() => new()
    {
        Host = "postal",
        Username = "dbr",
        Password = "secret",
        Domain = "removals.example.org",
    };

    [Fact]
    public void A_fully_configured_relay_is_accepted() => Configured().Validate();

    [Fact]
    public void There_is_no_built_in_credential()
    {
        var options = Configured();
        options.Username = string.Empty;
        options.Password = string.Empty;

        var problem = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("no built-in credential", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_default_domain()
    {
        // A default would be a domain this operator does not own, and every reply to every
        // demand would go to it.
        var options = Configured();
        options.Domain = string.Empty;

        var problem = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Mail:Domain is required", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://removals.example.org")]
    [InlineData("removals@example.org")]
    [InlineData("localhost")]
    public void A_domain_no_reply_could_come_back_to_is_refused_at_startup(string domain)
    {
        // Startup rather than first send: the address is wrong for every company at once,
        // and the alternative is discovering it on the demand somebody is waiting on.
        var options = Configured();
        options.Domain = domain;

        var problem = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("cannot carry a return address", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Submission_is_the_default_port()
    {
        // 587, not 25. Port 25 is where servers talk to each other, and a great many
        // networks block it outbound precisely because that is what a compromised host uses.
        Assert.Equal(587, new MailOptions().Port);
    }

    [Fact]
    public void Encryption_is_on_unless_it_is_turned_off() => Assert.True(new MailOptions().RequireTls);

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void A_port_that_is_not_a_port_is_refused(int port)
    {
        var options = Configured();
        options.Port = port;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
