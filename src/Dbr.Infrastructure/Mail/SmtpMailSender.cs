// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace Dbr.Infrastructure.Mail;

/// <summary>
/// Hands messages to an SMTP relay.
/// </summary>
/// <remarks>
/// <para>
/// SMTP rather than a vendor's HTTP API, which is the choice that keeps the relay
/// replaceable: every open-source mail server speaks it, so an operator who prefers a
/// different one changes a hostname rather than waiting for an adapter to be written.
/// </para>
/// <para>
/// <b>A connection per message, not a pooled one.</b> Demands are paced by the lane of the
/// company they are for, so they arrive here at intervals measured in seconds at best and
/// often much longer — which is exactly the shape a held-open SMTP connection is worst at.
/// Relays drop idle sessions without saying so, and the failure that produces lands on the
/// next message rather than on the idle period, turning one dropped connection into one
/// wrongly-failed demand. Reconnecting costs a round trip on a path that has already
/// waited out a rate limiter.
/// </para>
/// </remarks>
public sealed class SmtpMailSender : IMailSender
{
    private readonly MailOptions _options;
    private readonly ILogger<SmtpMailSender> _logger;

    public SmtpMailSender(IOptions<MailOptions> options, ILogger<SmtpMailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MailReceipt> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = Compose(message, out var messageId);

        using var client = new SmtpClient();
        try
        {
            // StartTlsWhenAvailable rather than StartTls when TLS is not required, so the
            // setting reads as "encrypt if the relay offers it" instead of "do not encrypt":
            // an operator who turned it off for a relay on the container network still gets
            // encryption from any relay that supports it.
            var security = _options.RequireTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(_options.Host, _options.Port, security, cancellationToken)
                .ConfigureAwait(false);
            await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken)
                .ConfigureAwait(false);
            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller is shutting down or gave up waiting. Not a delivery outcome, and
            // reporting it as one would spend an attempt from the demand's budget on a
            // decision the demand had no part in.
            throw;
        }
        catch (SmtpCommandException exception)
        {
            // The relay answered with a code, which is the one case where the transient
            // question has an authoritative answer rather than a guess.
            var transient = (int)exception.StatusCode is >= 400 and < 500;

            throw new MailDeliveryException(
                $"The relay refused the message with {(int)exception.StatusCode}.",
                transient,
                exception);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or SslHandshakeException)
        {
            // The relay cannot be talked to the way this instance is configured to talk to
            // it — encryption was required and not offered, or the certificate did not
            // stand up. Not transient, because another attempt reaches the same relay with
            // the same settings, and it is a settings fault rather than a demand's: every
            // company this instance writes to fails identically until somebody changes one
            // of the two ends.
            throw new MailDeliveryException(
                "The relay could not be reached on the terms this instance requires: "
                + exception.Message
                + " Either the relay needs encryption enabled, or Mail:RequireTls has to be "
                + "turned off — which is only safe when the relay is reached over a network "
                + "nothing else is on.",
                transient: false,
                exception);
        }
        catch (Exception exception) when (
            exception is SmtpProtocolException
                or AuthenticationException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException)
        {
            // Nothing was refused; the conversation did not complete. Authentication is in
            // here and treated as worth retrying even though a wrong password never becomes
            // right, because the failure this actually catches in a running system is a
            // relay restarting mid-handshake — and the wrong-password case is caught at
            // startup by the settings that must be present rather than by burning a demand.
            throw new MailDeliveryException(
                "The message was not handed over: " + exception.Message,
                transient: true,
                exception);
        }

        _logger.LogInformation(
            "Handed {MessageId} to the relay for job {JobId}.",
            messageId,
            message.From.JobId);

        return new MailReceipt(messageId);
    }

    /// <summary>
    /// Builds the message, and fixes the id it will be known by.
    /// </summary>
    /// <remarks>
    /// The id is generated here rather than left to the relay so that what is returned is
    /// what was actually sent. A relay that assigns its own would leave this reporting an id
    /// no header carries, which is worth nothing on the day a company is asked to look a
    /// demand up.
    /// <para>
    /// <c>Reply-To</c> is not set, and that is not an omission: the sender is already the
    /// address answers belong at, and a second one would be a second spelling of the same
    /// fact for a company's mail client to choose between.
    /// </para>
    /// </remarks>
    private static MimeMessage Compose(OutboundMessage message, out string messageId)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.From.Address));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        messageId = MimeUtils.GenerateMessageId();
        mime.MessageId = messageId;

        return mime;
    }
}
