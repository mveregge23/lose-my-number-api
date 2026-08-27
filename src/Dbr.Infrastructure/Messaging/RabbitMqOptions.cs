// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Messaging;

/// <summary>Where the queue is, and who to be on it.</summary>
/// <remarks>
/// The host has a default because compose provides one and a self-hoster following the
/// quickstart never sets it. The credentials do not: a broker reachable with whatever
/// happened to be compiled in is the kind of default that survives into a deployment
/// nobody meant to expose.
/// </remarks>
public sealed class RabbitMqOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public ushort Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Fails startup on settings no bus could connect with, rather than on the first
    /// message.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Host is required — it is where the queue lives. docker-compose.yml "
                + "sets it for every service in the stack.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Username and {SectionName}:Password are both required. There is no "
                + "built-in credential on purpose — one would work everywhere, including somewhere "
                + "nobody meant it to.");
        }
    }
}
