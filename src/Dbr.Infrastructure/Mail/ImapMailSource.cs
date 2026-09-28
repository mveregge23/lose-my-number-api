// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
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
/// <b>Headers on a pass, prose only on demand.</b> The envelope and the two threading
/// headers are everything that decides which attempt a message answers, and most of what
/// arrives in a mailbox answers none. So a pass downloads those, and the body is fetched
/// afterwards for the few messages that turned out to be answers — which means the prose
/// of everything else, including whatever else lands in an operator's mailbox, is never
/// read by this process at all. What is fetched is matched against a company's declared
/// phrases and dropped; a company's reply quotes the request it answers, and the safest
/// thing to do with that is not to keep it.
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
public sealed partial class ImapMailSource(
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

    public async Task<string?> ReadBodyAsync(
        InboundMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var client = new ImapClient();

        // Read-only: fetching a message body must not be what marks it read. Acknowledging
        // is a separate act that happens after something has been done with it, and a
        // connection opened to look at prose should not be able to end that.
        var folder = await OpenAsync(client, FolderAccess.ReadOnly, cancellationToken)
            .ConfigureAwait(false);

        if (!TryReadRef(message.SourceRef, folder.UidValidity, out var uid))
        {
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var mime = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // The plain part when there is one. A company that sends only HTML gets its tags
        // taken out crudely, which is enough for matching a phrase and is not enough for
        // anything else — which is fine, because matching a phrase is the whole of what
        // this is for and nothing keeps the result.
        return mime.TextBody ?? Flattened(mime.HtmlBody);
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
    /// HTML with its tags removed, well enough to look for a sentence in.
    /// </summary>
    /// <remarks>
    /// Deliberately crude. A real parse would be better prose and is not needed: the only
    /// consumer is a phrase match, and the result is never stored, displayed or sent
    /// anywhere. Entities are left alone for the same reason — a company whose boilerplate
    /// hinges on an ampersand gets read as unclear, which means a person looks at it.
    /// </remarks>
    private static string? Flattened(string? html) =>
        html is null ? null : TagPattern().Replace(html, " ");

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

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
