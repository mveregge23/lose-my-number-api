// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.Mail;

/// <summary>
/// Reads replies out of an ordinary mail account over IMAP.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter that needs nothing.</b> No domain, no MX record, no inbound port, no
/// server to run: an account at any mail provider and a password. That is what lets a
/// deployment read its own replies on the day it is set up, and it is why this one is
/// built first — the flow behind the port can be exercised against a real company's real
/// answers long before anybody decides which provider a production instance sends through.
/// </para>
/// <para>
/// <b>Headers only.</b> The envelope and the two threading headers are everything that
/// decides which attempt a message answers. The body is not downloaded, which is not
/// merely thrift: a company's reply quotes the request it answers, so the prose is as
/// likely to carry somebody's identity as the demand was, and a process that never fetches
/// it cannot spill it. Whatever reads it later should be the thing that decides where it
/// may go.
/// </para>
/// <para>
/// <b>Seen is the acknowledgement.</b> Unread means not yet filed, which is a flag the
/// server keeps for us and which survives this process being restarted, redeployed or
/// replaced. It also means an operator can put a reply back in front of this by marking it
/// unread — the ordinary way anybody expects a mailbox to behave — and that a message read
/// by hand in a mail client will not be filed, which is the sharp edge of the choice and
/// worth stating in the quickstart.
/// </para>
/// <para>
/// <b>A connection per pass.</b> Held open, an IMAP connection is a thing that quietly
/// dies between polls a minute apart and has to be noticed and rebuilt; opened per pass, a
/// failure is one pass's failure and the next one starts clean. A minute apart, the cost is
/// a handshake nobody is waiting on.
/// </para>
/// </remarks>
public sealed class ImapMailSource(
    IOptions<InboundMailOptions> options,
    ILogger<ImapMailSource> logger) : IInboundMailSource
{
    private readonly InboundMailOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<InboundMessage>> FetchAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var client = new ImapClient();
        var folder = await OpenAsync(client, FolderAccess.ReadOnly, cancellationToken)
            .ConfigureAwait(false);

        var unread = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken)
            .ConfigureAwait(false);

        if (unread.Count == 0)
        {
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return [];
        }

        // Oldest first, and only as many as the caller asked for: a mailbox that has been
        // accumulating since before this feature existed should drain over several passes
        // rather than in one pass that holds a connection open for all of them.
        var wanted = unread.OrderBy(uid => uid.Id).Take(limit).ToArray();

        var summaries = await folder
            .FetchAsync(wanted, MessageSummaryItems.Envelope | MessageSummaryItems.References, cancellationToken)
            .ConfigureAwait(false);

        var messages = new List<InboundMessage>(summaries.Count);

        foreach (var summary in summaries)
        {
            var message = Read(summary, folder.UidValidity);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        return messages;
    }

    public async Task AcknowledgeAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var client = new ImapClient();
        var folder = await OpenAsync(client, FolderAccess.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

        if (!TryReadRef(message.SourceRef, folder.UidValidity, out var uid))
        {
            // The mailbox was renumbered between reading this message and acknowledging it,
            // which is what UIDVALIDITY changing means. The messages are still there under
            // new ids and will be offered again; filing refuses the duplicates, so the
            // result is noise in a log rather than a reply counted twice.
            logger.LogWarning(
                "Cannot acknowledge {SourceRef}: the mailbox has been renumbered since it "
                + "was read. It will be offered again and refused as already filed.",
                message.SourceRef);

            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken)
            .ConfigureAwait(false);

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMailFolder> OpenAsync(
        ImapClient client,
        FolderAccess access,
        CancellationToken cancellationToken)
    {
        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            SecureSocketOptions.SslOnConnect,
            cancellationToken).ConfigureAwait(false);

        await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken)
            .ConfigureAwait(false);

        var folder = _options.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(_options.Folder, cancellationToken).ConfigureAwait(false);

        await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);

        return folder;
    }

    /// <summary>
    /// One summary as this port's message, or <see langword="null"/> when it cannot be one.
    /// </summary>
    /// <remarks>
    /// A message with no sender is not something a reply can be resolved from or filed
    /// against, and there is no useful thing to do with it beyond leaving it where it is.
    /// Everything else a message might be missing — an id of its own, a subject, either
    /// threading header — is ordinary, and the resolution that follows is allowed to find
    /// nothing.
    /// </remarks>
    private InboundMessage? Read(IMessageSummary summary, uint uidValidity)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault()?.Address;

        if (summary.UniqueId.Id == 0 || string.IsNullOrWhiteSpace(from))
        {
            logger.LogWarning(
                "A message in {Folder} has no sender this can read, and is left unread.",
                _options.Folder);

            return null;
        }

        var references = summary.References is { Count: > 0 }
            ? summary.References.Select(MessageIdentifier.Bare).OfType<string>().ToArray()
            : [];

        return new InboundMessage
        {
            SourceRef = $"{uidValidity}-{summary.UniqueId.Id}",
            From = from,
            To = envelope!.To.Mailboxes.Select(mailbox => mailbox.Address).ToArray(),
            MessageId = MessageIdentifier.Bare(envelope.MessageId),
            InReplyTo = MessageIdentifier.Bare(envelope.InReplyTo),
            References = references,
            Subject = envelope.Subject,
            ReceivedAt = summary.InternalDate ?? envelope.Date ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// The message a source reference names in this mailbox, if it still names one.
    /// </summary>
    /// <remarks>
    /// The reference carries the folder's UIDVALIDITY as well as the message's id because
    /// a server is entitled to renumber a folder, after which the same number means a
    /// different message. Acknowledging the wrong message would mark a reply nobody has
    /// filed as dealt with, and it would never be offered again.
    /// </remarks>
    private static bool TryReadRef(string sourceRef, uint uidValidity, out UniqueId uid)
    {
        uid = UniqueId.Invalid;

        var separator = sourceRef.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!uint.TryParse(sourceRef.AsSpan(0, separator), out var validity)
            || validity != uidValidity
            || !uint.TryParse(sourceRef.AsSpan(separator + 1), out var id)
            || id == 0)
        {
            return false;
        }

        uid = new UniqueId(uidValidity, id);
        return true;
    }
}
