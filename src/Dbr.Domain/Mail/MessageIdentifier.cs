// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// The one spelling of a message id everything here compares against.
/// </summary>
/// <remarks>
/// <para>
/// A header writes an id inside angle brackets and the id is what sits between them. An
/// attempt stores the id it sent under bare, so a header value has to be reduced to that
/// form before the two can be compared at all — and doing it in one place is what stops
/// half the code from storing one spelling while the other half looks up the other.
/// </para>
/// <para>
/// The failure this prevents is quiet: a reply whose id fails to match is a reply filed
/// against no attempt, which reads exactly like a company that never answered — the one
/// outcome the whole of this work exists to stop being indistinguishable from the others.
/// </para>
/// </remarks>
public static class MessageIdentifier
{
    /// <summary>
    /// The id inside a header value, or <see langword="null"/> when there is not one.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow. It takes the brackets off and rejects what cannot be an id at
    /// all; it does not try to decide whether a well-formed id is one anything here
    /// generated, because that question is answered by looking it up rather than by
    /// reading it.
    /// </remarks>
    public static string? Bare(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var trimmed = header.Trim();

        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        // An id has one address-shaped part and nothing that would have needed quoting. A
        // value with a space in it arrived folded or mangled, and guessing at where it was
        // broken would invent a match rather than find one.
        if (trimmed.Length == 0
            || trimmed.Length > 512
            || trimmed.Any(char.IsWhiteSpace)
            || trimmed.Contains('<', StringComparison.Ordinal)
            || trimmed.Contains('>', StringComparison.Ordinal)
            || trimmed.IndexOf('@', StringComparison.Ordinal) <= 0
            || trimmed.EndsWith('@'))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Every id in a header that may hold several, in the order it holds them.
    /// </summary>
    /// <remarks>
    /// <c>References</c> is a list, and the order is the thread's: the first entry is what
    /// started it and the last is what was answered. Kept in order and deduplicated,
    /// because a long thread repeats ids and the lookup that follows should ask about each
    /// one once.
    /// </remarks>
    public static IReadOnlyList<string> All(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        var found = new List<string>();

        foreach (var candidate in header.Split('<', StringSplitOptions.RemoveEmptyEntries))
        {
            var close = candidate.IndexOf('>', StringComparison.Ordinal);
            if (close <= 0)
            {
                continue;
            }

            var bare = Bare(candidate[..close]);
            if (bare is not null && !found.Contains(bare, StringComparer.Ordinal))
            {
                found.Add(bare);
            }
        }

        return found;
    }
}
