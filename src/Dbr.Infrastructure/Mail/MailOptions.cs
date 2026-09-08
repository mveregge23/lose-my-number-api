// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Infrastructure.Mail;

/// <summary>Where the relay is, who to be on it, and the domain answers come back to.</summary>
/// <remarks>
/// <para>
/// The host has a default because compose provides one and a self-hoster following the
/// quickstart never sets it. The credentials and the domain do not, and for different
/// reasons: a relay reachable with whatever happened to be compiled in is the kind of
/// default that survives into a deployment nobody meant to expose, and a default domain
/// would be somebody else's — every demand this instance sent would carry a return address
/// pointing at a domain its operator does not own, and every reply would go there.
/// </para>
/// <para>
/// <see cref="Domain"/> is separate from <see cref="Host"/> and cannot be derived from it.
/// The host is where messages are handed over and the domain is what the world answers to;
/// they are the same string only in the simplest deployment, and an instance relaying
/// through a smarthost has two entirely unrelated ones.
/// </para>
/// </remarks>
public sealed class MailOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Mail";

    /// <summary>The relay to hand messages to.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// The port to hand them over on.
    /// </summary>
    /// <remarks>
    /// 587 — submission, where an authenticated client belongs. Not 25, which is where mail
    /// servers talk to each other and which a great many networks block outbound precisely
    /// because that is what a compromised host uses.
    /// </remarks>
    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The domain whose catch-all this instance reads, and the half of every return address
    /// that is not the job.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Whether the connection must be encrypted before anything is sent.
    /// </summary>
    /// <remarks>
    /// Defaults on, and settable only so that a relay reached over a container network can
    /// be run without a certificate for a hostname that resolves nowhere else. Turning it
    /// off on a relay reached across a network hands the credentials below, and the body of
    /// every demand, to anything on the path — so the setting exists, and startup says so
    /// when it is off and the relay is not local.
    /// </remarks>
    public bool RequireTls { get; set; } = true;

    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Host is required — it is the relay demands are handed to. "
                + "docker-compose.yml sets it for every service in the stack.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Port must be a port, and is {Port}.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Username and {SectionName}:Password are both required. There is "
                + "no built-in credential on purpose — one would work everywhere, including "
                + "somewhere nobody meant it to. A relay that accepts unauthenticated mail from "
                + "this instance accepts it from everything else that can reach it.");
        }

        if (string.IsNullOrWhiteSpace(Domain))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Domain is required — it is the domain every demand's return "
                + "address is on, and it has no safe default because a default would be a "
                + "domain this operator does not own. Replies to it are how a company's answer "
                + "reaches the job that asked.");
        }

        // Built rather than pattern-matched: the mailbox type already decides what it can
        // make an address out of, and a second opinion here is a second thing to disagree
        // with it on the day one of them is loosened.
        try
        {
            _ = JobMailbox.For(Guid.NewGuid(), Domain);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Domain is '{Domain}', which cannot carry a return address. "
                + "It should be the bare domain whose catch-all this instance reads, without a "
                + "scheme, a local part, or a trailing path.",
                exception);
        }
    }
}
