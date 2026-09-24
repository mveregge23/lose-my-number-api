// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Mail;

/// <summary>
/// Where replies are read from, and by which of the ways of reading them.
/// </summary>
/// <remarks>
/// <para>
/// Its own section rather than more fields on the outbound settings, because the two are
/// genuinely different deployments of the same idea: an instance may send through a
/// smarthost it does not own while reading a mailbox it does, and the day they are one
/// server is the simplest case rather than the general one.
/// </para>
/// <para>
/// <b>Off unless somebody turns it on.</b> The opposite of the dispatchers, and for the
/// opposite reason: a scan nobody starts is work not done, while a mailbox nobody meant to
/// give this process the password to is a mailbox being read. Reading somebody's mail
/// should take a deliberate act.
/// </para>
/// </remarks>
public sealed class InboundMailOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "InboundMail";

    /// <summary>The mailbox adapter: IMAP against an ordinary mail account.</summary>
    public const string MailboxProvider = "mailbox";

    /// <summary>The provider that owns the domain and pushes replies instead.</summary>
    public const string WebhookProvider = "webhook";

    /// <summary>Whether this process reads replies at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which way of reading them.</summary>
    public string Provider { get; set; } = MailboxProvider;

    /// <summary>The IMAP server to read from.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The port to read on.
    /// </summary>
    /// <remarks>
    /// 993 — IMAP over TLS from the first byte. Not 143, which starts in the clear and
    /// upgrades, and which would hand the password below to anything on the path if the
    /// upgrade were stripped.
    /// </remarks>
    public int Port { get; set; } = 993;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The folder replies arrive in.
    /// </summary>
    /// <remarks>
    /// Settable because an operator who files this instance's mail into its own folder with
    /// a server-side rule has a tidier mailbox and the same behaviour, and because the
    /// inbox is not called <c>INBOX</c> everywhere.
    /// </remarks>
    public string Folder { get; set; } = "INBOX";

    /// <summary>How long between passes.</summary>
    /// <remarks>
    /// A minute rather than the dispatchers' fifteen seconds. Nothing is waiting on this
    /// the way a queued demand waits on a dispatcher: a company's answer has already taken
    /// hours or days, and polling a mailbox four times a minute would spend an account's
    /// connection allowance to learn nothing sooner than it matters.
    /// </remarks>
    public int PollSeconds { get; set; } = 60;

    /// <summary>How many messages one pass reads.</summary>
    public int BatchSize { get; set; } = 25;

    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (Provider == WebhookProvider)
        {
            // Refusing rather than accepting and doing nothing. An adapter that quietly
            // never produced a reply would leave every demand reading as unanswered, which
            // is precisely the state this whole capability exists to distinguish from — and
            // it would look identical to a deployment that was working and had no mail.
            throw new InvalidOperationException(
                $"{SectionName}:Provider is '{WebhookProvider}', and no provider has been "
                + "chosen or built behind it yet. The requirements a candidate has to meet "
                + "are written down; until one is picked, read replies with "
                + $"Provider={MailboxProvider}, which needs no domain. Leaving this set "
                + "would start a process that reads no replies and reports none, which is "
                + "indistinguishable from a company that never answered.");
        }

        if (Provider != MailboxProvider)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Provider is '{Provider}', which is not a way of reading "
                + $"replies this build has. It is '{MailboxProvider}' or "
                + $"'{WebhookProvider}'.");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Host is required — it is the server replies are read from. "
                + "There is no default, because a default would be somebody else's mail "
                + "server.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Port must be a port, and is {Port}.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Username and {SectionName}:Password are both required. A "
                + "mailbox is not read anonymously, and half a credential fails at the "
                + "first pass rather than at startup.");
        }

        if (string.IsNullOrWhiteSpace(Folder))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Folder is required, and is INBOX unless a deployment files "
                + "this instance's mail somewhere else.");
        }

        if (PollSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:PollSeconds must be at least a second, and is {PollSeconds}.");
        }

        if (BatchSize < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BatchSize must be at least one message, and is {BatchSize}.");
        }
    }
}
