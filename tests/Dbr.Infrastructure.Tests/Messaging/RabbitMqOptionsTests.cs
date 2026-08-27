// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Messaging;

namespace Dbr.Infrastructure.Tests.Messaging;

/// <summary>What the bus refuses to start without.</summary>
public class RabbitMqOptionsTests
{
    [Fact]
    public void A_fully_configured_broker_is_accepted() =>
        new RabbitMqOptions { Host = "rabbitmq", Username = "dbr", Password = "secret" }.Validate();

    [Fact]
    public void There_is_no_built_in_credential()
    {
        // A default that works everywhere works somewhere nobody meant it to. The host has
        // one because compose supplies it and a self-hoster never types it; the credential
        // does not, for the same reason the terms version and the signing key do not.
        var options = new RabbitMqOptions { Host = "rabbitmq" };

        var problem = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("no built-in credential", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("dbr", "")]
    [InlineData("dbr", "   ")]
    public void Half_a_credential_is_not_a_credential(string username, string password)
    {
        var options = new RabbitMqOptions { Host = "rabbitmq", Username = username, Password = password };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_blank_host_is_refused_by_name()
    {
        var options = new RabbitMqOptions { Host = " ", Username = "dbr", Password = "secret" };

        var problem = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("RabbitMq:Host", problem.Message, StringComparison.Ordinal);
    }
}
