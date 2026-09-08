// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.Mail;

/// <summary>
/// The instance's own mail domain, applied to every address built or read.
/// </summary>
/// <remarks>
/// Reads the domain once at construction rather than per call. It is not a setting that
/// can change usefully while the process runs — a demand already sent carries the address
/// it was sent with, and a reply to it arrives addressed to that one long after any reload
/// — so re-reading it would only make it possible for two messages in the same process to
/// disagree about what this instance is called.
/// </remarks>
public sealed class JobMailboxes : IJobMailboxes
{
    private readonly string _domain;

    public JobMailboxes(IOptions<MailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _domain = options.Value.Domain;
    }

    public JobMailbox For(Guid jobId) => JobMailbox.For(jobId, _domain);

    public bool TryResolve(string? address, out Guid jobId) =>
        JobMailbox.TryResolve(address, _domain, out jobId);
}
