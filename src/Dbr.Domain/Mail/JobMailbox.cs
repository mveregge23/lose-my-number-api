// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// The address one attempt speaks from, and the job an answer to it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// One type for both directions on purpose. The address a demand is sent from and the
/// address a company's reply is resolved by are the same string, and building it in one
/// place and reading it in another is how the two come to disagree — silently, and in the
/// direction nobody notices, because a reply that fails to resolve looks exactly like a
/// company that never answered.
/// </para>
/// <para>
/// <b>A local part rather than a subaddress.</b> The obvious spelling is
/// <c>removals+{jobId}@</c> and it is avoided. A <c>+</c> is visibly an alias: enough mail
/// systems strip it and enough form validators reject it as disposable that the address
/// would fail precisely where a demand has to work, and it would fail quietly — a demand
/// that never arrived and one that was ignored look identical from here while the deadline
/// runs on both. A unique local part correlates exactly as well, since the recipient still
/// names one job, and costs only a domain routed as a catch-all rather than one named
/// mailbox.
/// </para>
/// <para>
/// <b>Nothing here is a secret.</b> The address goes to a company in the open, so knowing
/// it is not authority to do anything: it names a job, and whoever resolves one is expected
/// to check that the job is actually waiting on an answer before treating a message as one.
/// Correlation is what this provides, and the whole of it.
/// </para>
/// </remarks>
public sealed record JobMailbox
{
    /// <summary>
    /// What every one of these local parts starts with.
    /// </summary>
    /// <remarks>
    /// Fixed rather than configurable. It is the half of the address that says which
    /// system's catch-all is being read, and an operator who changed it would change the
    /// addresses of every demand already in flight — whose replies are already addressed to
    /// the old spelling, and would stop resolving on the day of the change.
    /// </remarks>
    public const string Prefix = "removals-";

    private JobMailbox(Guid jobId, string address)
    {
        JobId = jobId;
        Address = address;
    }

    /// <summary>The attempt this address belongs to.</summary>
    public Guid JobId { get; }

    /// <summary>The address itself, in the form a message carries it.</summary>
    public string Address { get; }

    /// <summary>
    /// The address this job speaks from.
    /// </summary>
    /// <param name="jobId">The attempt. Never empty: a demand no answer can be tied back to.</param>
    /// <param name="domain">The domain whose catch-all this instance reads.</param>
    /// <exception cref="ArgumentException">The job or the domain cannot make an address.</exception>
    public static JobMailbox For(Guid jobId, string domain)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException(
                "A mailbox needs a job to name. The empty id would give every demand the same "
                + "return address, and the first reply would resolve to all of them.",
                nameof(jobId));
        }

        var normalized = NormalizeDomain(domain);
        if (normalized is null)
        {
            throw new ArgumentException(
                $"'{domain}' cannot be the mail domain. It is the half of the return address a "
                + "company replies to, so a blank or malformed one produces demands nobody can "
                + "answer.",
                nameof(domain));
        }

        return new JobMailbox(jobId, $"{Prefix}{jobId:D}@{normalized}");
    }

    /// <summary>
    /// The job an incoming recipient names, if it names one of ours at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answering <see langword="false"/> is the ordinary case rather than the exceptional
    /// one. A catch-all domain accepts every recipient, so most of what arrives at one is
    /// addressed to something that was never a job — which is why this reports rather than
    /// throws, and why the domain is checked here instead of being taken on trust from
    /// whatever delivered the message.
    /// </para>
    /// <para>
    /// The local part is compared without case. Mail local parts are case-sensitive by the
    /// letter of the standard and are lowercased by a great deal of software in practice,
    /// and a reply that failed to resolve because something in the path changed a letter
    /// would be indistinguishable from a company that stayed silent.
    /// </para>
    /// </remarks>
    /// <param name="address">A recipient, as it arrived.</param>
    /// <param name="domain">The domain this instance accepts answers on.</param>
    /// <param name="jobId">The attempt named, when there is one.</param>
    public static bool TryResolve(string? address, string domain, out Guid jobId)
    {
        jobId = Guid.Empty;

        var expected = NormalizeDomain(domain);
        if (expected is null || string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
        {
            return false;
        }

        if (!trimmed.AsSpan(at + 1).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var localPart = trimmed.AsSpan(0, at);
        if (!localPart.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParseExact(localPart[Prefix.Length..], "D", out jobId) && jobId != Guid.Empty;
    }

    /// <summary>
    /// The domain in the one spelling everything here compares against, or
    /// <see langword="null"/> when it could not be one.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow: it rejects what cannot be a domain in an address rather than
    /// trying to decide what is a real one. Whether the domain resolves, and whether its
    /// catch-all reaches this instance, are facts about DNS and the relay's routing — a
    /// stricter check here would reject valid deployments to catch nothing a misdelivered
    /// message would not catch first.
    /// </remarks>
    private static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var normalized = domain.Trim().TrimEnd('.');
        var labels = normalized.Split('.');

        // At least two labels, because a single one is a host on somebody's local network
        // rather than a domain the outside world can reply to.
        if (labels.Length < 2)
        {
            return null;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63
                || label[0] == '-'
                || label[^1] == '-')
            {
                return null;
            }

            foreach (var character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return null;
                }
            }
        }

        return normalized;
    }

    /// <summary>The address, which is the whole of what this is.</summary>
    public override string ToString() => Address;
}

/// <summary>
/// Resolves the mailbox a job speaks from, without every caller holding the domain.
/// </summary>
/// <remarks>
/// The domain is configuration, and the alternative is passing it down through the
/// dispatcher into every connector that composes a message — which would put a settable
/// string on the path of something whose whole job is to be the same on both sides of a
/// conversation. A connector asks for the mailbox of the job it was handed and gets one
/// built from the instance's own setting.
/// </remarks>
public interface IJobMailboxes
{
    /// <summary>The address this attempt speaks from.</summary>
    JobMailbox For(Guid jobId);

    /// <summary>The job this recipient names, when it names one at all.</summary>
    bool TryResolve(string? address, out Guid jobId);
}
