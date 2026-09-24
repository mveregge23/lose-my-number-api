// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Mail;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>
/// What a deployment has to say before this process will read anybody's mail.
/// </summary>
public class InboundMailOptionsTests
{
    /// <summary>
    /// Turned off, nothing else has to be said.
    /// </summary>
    /// <remarks>
    /// Which is the default, and the opposite of the dispatchers: a scan nobody starts is
    /// work left undone, and a mailbox nobody meant to hand over the password to is a
    /// mailbox being read.
    /// </remarks>
    [Fact]
    public void Reading_replies_is_off_until_a_deployment_asks_for_it()
    {
        var options = new InboundMailOptions();

        Assert.False(options.Enabled);
        options.Validate();
    }

    /// <summary>
    /// The provider nobody has chosen refuses to start rather than doing nothing.
    /// </summary>
    /// <remarks>
    /// The whole reason this assertion exists. An adapter that accepted the setting and
    /// quietly produced no replies would leave every demand reading as unanswered — which
    /// is exactly the state this capability was built to distinguish from, and which looks
    /// identical to a deployment that is working and has no mail. A process that will not
    /// start says what is wrong on the day it is configured.
    /// </remarks>
    [Fact]
    public void The_provider_that_has_not_been_chosen_refuses_to_start()
    {
        var options = new InboundMailOptions
        {
            Enabled = true,
            Provider = InboundMailOptions.WebhookProvider,
        };

        var refusal = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(InboundMailOptions.MailboxProvider, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_way_of_reading_replies_this_build_does_not_have_is_refused()
    {
        var options = new InboundMailOptions { Enabled = true, Provider = "carrier-pigeon" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("", "reader", "secret")]
    [InlineData("imap.test", "", "secret")]
    [InlineData("imap.test", "reader", "")]
    public void A_mailbox_missing_half_its_settings_is_refused_at_startup(
        string host,
        string username,
        string password)
    {
        var options = new InboundMailOptions
        {
            Enabled = true,
            Host = host,
            Username = username,
            Password = password,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_configured_mailbox_is_accepted()
    {
        var options = new InboundMailOptions
        {
            Enabled = true,
            Host = "imap.test",
            Username = "reader",
            Password = "secret",
        };

        options.Validate();
    }
}
