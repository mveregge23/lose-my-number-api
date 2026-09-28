// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// What a company's answer turned out to say.
/// </summary>
/// <remarks>
/// Four rather than three, and the fourth is the default. A reply nothing recognises is not
/// a failure of this system so much as the ordinary state of a conversation with a company
/// nobody has written a phrase for yet — and the only safe thing to do with one is put it
/// in front of a person.
/// </remarks>
public enum ReplyReading
{
    /// <summary>Nothing declared for this company matched, so a person should read it.</summary>
    Unclear,

    /// <summary>The company is asking for something before it will act.</summary>
    NeedsUs,

    /// <summary>The company says it will not act.</summary>
    Refused,

    /// <summary>The company says it has done what was asked.</summary>
    Confirmed,
}

/// <summary>
/// The phrases one company's answers are recognised by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phrases and not patterns.</b> A regular expression in a contributed document is a
/// thing a reviewer has to simulate in their head to approve, and one of the ways it can be
/// wrong is to run for a very long time on input a company chooses. A phrase is read at a
/// glance, matched without case, and can be wrong in exactly one way: it matches the wrong
/// letter, which is what the reading being conservative is for.
/// </para>
/// <para>
/// <b>Declared per company rather than guessed at generally.</b> Companies write their own
/// boilerplate and there is no sentence that means "removed" everywhere; a general
/// heuristic would be a guess whose most expensive failure is marking a demand done that
/// nobody has acted on. So a company's own file says how that company talks, reviewed the
/// way everything else about a company is, and a company nobody has written phrases for
/// gets every reply read by a person.
/// </para>
/// </remarks>
/// <param name="Confirmed">Phrases that mean the company says it has acted.</param>
/// <param name="Refused">Phrases that mean it says it will not.</param>
/// <param name="NeedsUs">Phrases that mean it wants something before it does anything.</param>
public sealed record ReplyPhrases(
    IReadOnlyList<string> Confirmed,
    IReadOnlyList<string> Refused,
    IReadOnlyList<string> NeedsUs)
{
    /// <summary>What a company nobody has written phrases for has.</summary>
    public static ReplyPhrases None { get; } = new([], [], []);

    /// <summary>Whether this says anything at all about how a company talks.</summary>
    public bool IsEmpty => Confirmed.Count == 0 && Refused.Count == 0 && NeedsUs.Count == 0;
}

/// <summary>What a reply was read as, and what made it read that way.</summary>
/// <param name="Reading">The verdict.</param>
/// <param name="MatchedPhrase">
/// The declared phrase that produced it, or <see langword="null"/> when nothing matched.
/// Catalog text rather than anything the company wrote about a person, which is what makes
/// it safe to keep beside the verdict.
/// </param>
public sealed record ReplyReadingResult(ReplyReading Reading, string? MatchedPhrase);

/// <summary>
/// Reads what a company wrote, and keeps the verdict rather than the prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prose is read and not stored, which is the decision this whole step turns on.</b>
/// The obvious middle course — keep a short excerpt so a person can see why — does not
/// survive contact with what companies actually send: the first real answer this system
/// received quoted the request back, identity block and all, within the first few lines. An
/// excerpt cannot be made safe by being short, because the part that is quoted first is
/// precisely the part worth not keeping. So the body is fetched, matched against the
/// phrases, and dropped; what remains is a verdict and the catalog phrase that produced it.
/// A person who needs the words themselves has the mailbox, which is theirs.
/// </para>
/// <para>
/// <b>Ranked by what acting on it would cost if it were wrong.</b> A company asking for
/// something wins over everything else: if any part of an answer says it is waiting on us,
/// then it is, whatever else the same message says — and a message that both confirms and
/// asks is a company answering two things at once, where the half that needs a person is
/// the half that must not be missed. A refusal comes next. A confirmation ranks last
/// deliberately, because it is the reading with the largest consequence and the worst
/// failure: a demand recorded as honoured that nobody has acted on stops being asked about
/// again.
/// </para>
/// </remarks>
public static class ReplyReadings
{
    /// <summary>What this company's phrases make of this message.</summary>
    public static ReplyReadingResult Read(string? body, ReplyPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);

        if (string.IsNullOrWhiteSpace(body) || phrases.IsEmpty)
        {
            return new ReplyReadingResult(ReplyReading.Unclear, null);
        }

        return Match(body, phrases.NeedsUs) is { } asking
            ? new ReplyReadingResult(ReplyReading.NeedsUs, asking)
            : Match(body, phrases.Refused) is { } refusing
                ? new ReplyReadingResult(ReplyReading.Refused, refusing)
                : Match(body, phrases.Confirmed) is { } confirming
                    ? new ReplyReadingResult(ReplyReading.Confirmed, confirming)
                    : new ReplyReadingResult(ReplyReading.Unclear, null);
    }

    /// <summary>
    /// The first of these phrases the message contains, if it contains one.
    /// </summary>
    /// <remarks>
    /// Without case, because boilerplate is generated and capitalisation is the first thing
    /// a template changes. Whitespace is collapsed first for the same class of reason: a
    /// mail client wraps prose at whatever width it likes, and a phrase that spans the wrap
    /// would match in one company's client and not in another's.
    /// </remarks>
    private static string? Match(string body, IReadOnlyList<string> phrases)
    {
        var collapsed = Collapse(body);

        foreach (var phrase in phrases)
        {
            if (collapsed.Contains(Collapse(phrase), StringComparison.OrdinalIgnoreCase))
            {
                return phrase;
            }
        }

        return null;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
