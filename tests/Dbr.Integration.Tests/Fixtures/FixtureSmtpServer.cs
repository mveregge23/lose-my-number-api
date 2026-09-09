// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>What one accepted message looked like on the wire.</summary>
/// <param name="From">The envelope sender, as <c>MAIL FROM</c> gave it.</param>
/// <param name="Recipients">The envelope recipients, as <c>RCPT TO</c> gave them.</param>
/// <param name="Data">The message itself, headers and body, exactly as it arrived.</param>
public sealed record FixtureMessage(string From, IReadOnlyList<string> Recipients, string Data)
{
    /// <summary>One header's value, or <see langword="null"/> when the message has none.</summary>
    /// <remarks>
    /// Reads the raw text rather than parsing it with MimeKit on purpose. The sender builds
    /// the message with MimeKit, and checking its work with the same library would agree
    /// with itself about anything the library does consistently — including getting it
    /// consistently wrong.
    /// </remarks>
    public string? Header(string name)
    {
        var prefix = name + ":";

        foreach (var line in Data.Split("\r\n"))
        {
            if (line.Length == 0)
            {
                // End of headers. A name appearing after this is body text that happens to
                // look like a header, which is exactly what a naive search would return.
                return null;
            }

            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>The part after the headers.</summary>
    public string Body
    {
        get
        {
            var separator = Data.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            return separator < 0 ? string.Empty : Data[(separator + 4)..];
        }
    }
}

/// <summary>
/// An SMTP server that accepts messages and keeps them, or refuses them with a code the
/// test chooses.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a package, for one reason: <b>the interesting tests are the
/// refusals.</b> Whether a demand is worth another attempt is decided by whether the relay
/// answered 4xx or 5xx, and asserting that needs a server that will answer exactly what the
/// test asks it to. A sink built to accept mail cooperatively is good at the case that was
/// never in doubt.
/// </para>
/// <para>
/// It speaks the smallest dialect the client needs and no more: a greeting, EHLO with an
/// AUTH advertisement, AUTH, MAIL, RCPT, DATA and QUIT. There is no TLS, which is itself
/// useful — it is how the test that <c>RequireTls</c> genuinely refuses to send in the clear
/// gets a server to refuse against.
/// </para>
/// </remarks>
public sealed class FixtureSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<FixtureMessage> _messages = [];
    private readonly Lock _gate = new();
    private readonly Task _accepting;
    private bool _stopped;

    private FixtureSmtpServer(TcpListener listener)
    {
        _listener = listener;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The port it is listening on, assigned by the operating system.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// The code to answer <c>MAIL FROM</c> with, or <see langword="null"/> to accept.
    /// </summary>
    /// <remarks>
    /// Set by a test to make the relay refuse. 4xx and 5xx are the two answers that matter,
    /// because they are what the sender turns into "worth another attempt" and "not".
    /// </remarks>
    public int? RefuseWith { get; set; }

    /// <summary>Whether to advertise AUTH at all.</summary>
    public bool OffersAuth { get; set; } = true;

    /// <summary>The credential the last client presented, decoded.</summary>
    public string? PresentedUser { get; private set; }

    /// <summary>Everything accepted so far.</summary>
    public IReadOnlyList<FixtureMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>Starts one on a port nothing else is using.</summary>
    public static FixtureSmtpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return new FixtureSmtpServer(listener);
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent, because a test that stops the server to prove the sender copes with
        // an unreachable relay then disposes it again on the way out.
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down is not a failure.
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            // Not awaited: a second client should not wait behind the first, and a session
            // that faults takes itself down rather than the listener.
            _ = Task.Run(() => ConverseAsync(client));
        }
    }

    private async Task ConverseAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            await writer.WriteLineAsync("220 fixture ESMTP").ConfigureAwait(false);

            var from = string.Empty;
            var recipients = new List<string>();

            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var verb = line.Split(' ')[0].ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        await writer.WriteLineAsync("250-fixture").ConfigureAwait(false);

                        if (OffersAuth)
                        {
                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN").ConfigureAwait(false);
                        }

                        await writer.WriteLineAsync("250 8BITMIME").ConfigureAwait(false);

                        break;

                    case "AUTH":
                        await AuthenticateAsync(line, reader, writer).ConfigureAwait(false);

                        break;

                    case "MAIL":
                        if (RefuseWith is { } code)
                        {
                            await writer.WriteLineAsync($"{code} refused by the fixture")
                                .ConfigureAwait(false);

                            break;
                        }

                        from = Address(line);
                        recipients.Clear();
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);

                        break;

                    case "RCPT":
                        recipients.Add(Address(line));
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);

                        break;

                    case "DATA":
                        await writer.WriteLineAsync("354 send it").ConfigureAwait(false);
                        var data = await ReadDataAsync(reader).ConfigureAwait(false);

                        lock (_gate)
                        {
                            _messages.Add(new FixtureMessage(from, [.. recipients], data));
                        }

                        await writer.WriteLineAsync("250 queued").ConfigureAwait(false);

                        break;

                    case "QUIT":
                        await writer.WriteLineAsync("221 bye").ConfigureAwait(false);

                        return;

                    default:
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);

                        break;
                }
            }
        }
    }

    private async Task AuthenticateAsync(string line, StreamReader reader, StreamWriter writer)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;

        if (mechanism == "PLAIN")
        {
            var payload = parts.Length > 2 ? parts[2] : null;

            if (payload is null)
            {
                await writer.WriteLineAsync("334 ").ConfigureAwait(false);
                payload = await reader.ReadLineAsync().ConfigureAwait(false);
            }

            // authzid NUL authcid NUL password — the middle field is the user.
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload ?? string.Empty));
            var fields = decoded.Split('\0');
            PresentedUser = fields.Length > 1 ? fields[1] : null;
        }
        else
        {
            await writer.WriteLineAsync("334 VXNlcm5hbWU6").ConfigureAwait(false);
            var user = await reader.ReadLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("334 UGFzc3dvcmQ6").ConfigureAwait(false);
            _ = await reader.ReadLineAsync().ConfigureAwait(false);

            PresentedUser = Encoding.UTF8.GetString(Convert.FromBase64String(user ?? string.Empty));
        }

        await writer.WriteLineAsync("235 authenticated").ConfigureAwait(false);
    }

    /// <summary>Reads until the lone dot, undoing the stuffing the client applied.</summary>
    private static async Task<string> ReadDataAsync(StreamReader reader)
    {
        var data = new StringBuilder();

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line == ".")
            {
                break;
            }

            // A body line that begins with a dot arrives with a second one in front of it,
            // so that it cannot be mistaken for the terminator.
            data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
            data.Append("\r\n");
        }

        return data.ToString();
    }

    /// <summary>The address out of <c>MAIL FROM:&lt;a@b&gt;</c> or <c>RCPT TO:&lt;a@b&gt;</c>.</summary>
    private static string Address(string line)
    {
        var open = line.IndexOf('<');
        var close = line.IndexOf('>');

        return open >= 0 && close > open ? line[(open + 1)..close] : string.Empty;
    }
}
