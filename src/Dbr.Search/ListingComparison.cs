// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search;

/// <summary>
/// How closely one line of text on a listing agrees with what is on file.
/// </summary>
/// <remarks>
/// <para>
/// <b>This decides agreement, not worth.</b> It answers "did the listing show the same
/// thing", in the three degrees the contract has, and stops there — what a set of agreements
/// is worth is a separate decision that has to mean the same thing on every company, and it
/// lives above the search line for that reason. Mixing the two here would put a per-broker
/// thumb on a scale that is supposed to be shared.
/// </para>
/// <para>
/// <b>Conflicting is a real answer and not a failure to match.</b> A listing that shows a
/// different surname is somebody else and says so; one that simply did not print a surname
/// says nothing. The first is reported, the second is absent, and collapsing them would make
/// a page that disagreed with everything look like a page that was merely uninformative.
/// </para>
/// </remarks>
public static class ListingComparison
{
    /// <summary>
    /// What the listing's text does against the names on file.
    /// </summary>
    /// <remarks>
    /// The surname carries the decision, because it is the half that rules people out. Two
    /// people sharing a given name is a coincidence; a different surname is a different
    /// family. So a matching surname with a shortened given name — Alex against Alexandra —
    /// is partial, and a different surname is a contradiction however well the rest reads.
    /// </remarks>
    public static MatchStrength? Names(string listing, IReadOnlyList<string> onFile)
    {
        ArgumentNullException.ThrowIfNull(onFile);

        if (Normalise(listing).Length == 0 || onFile.Count == 0)
        {
            return null;
        }

        MatchStrength? best = null;

        foreach (var seen in Readings(listing))
        {
            foreach (var name in onFile)
            {
                var known = Normalise(name);

                if (known.Length == 0)
                {
                    continue;
                }

                best = Stronger(best, CompareName(seen, known));
            }
        }

        return best;
    }

    /// <summary>
    /// The ways one printed name could be read.
    /// </summary>
    /// <remarks>
    /// <b>Because a comma in a name usually means the surname came first.</b> Directory
    /// listings print "Whitfield, Alex" constantly, and normalisation strips the comma without
    /// reordering the words — so the surname the comparison then examines is the given name,
    /// and the listing reads as a different person entirely. That is a false negative on the
    /// most ordinary formatting there is: somebody would simply not be told about a listing
    /// that names them.
    ///
    /// Only a single comma is treated this way. Two or more is an address, an "also known as"
    /// list, or a line that has had a city appended to it, and guessing at those would trade
    /// this false negative for a false positive.
    /// </remarks>
    private static IEnumerable<string> Readings(string listing)
    {
        yield return Normalise(listing);

        var parts = listing.Split(',');

        if (parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1]))
        {
            yield return Normalise($"{parts[1]} {parts[0]}");
        }
    }

    private static MatchStrength CompareName(string seen, string known)
    {
        if (string.Equals(seen, known, StringComparison.Ordinal))
        {
            return MatchStrength.Exact;
        }

        var seenParts = seen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var knownParts = known.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (seenParts.Length == 0 || knownParts.Length == 0)
        {
            return MatchStrength.Conflicting;
        }

        if (!string.Equals(seenParts[^1], knownParts[^1], StringComparison.Ordinal))
        {
            return MatchStrength.Conflicting;
        }

        var seenGiven = seenParts[0];
        var knownGiven = knownParts[0];

        if (string.Equals(seenGiven, knownGiven, StringComparison.Ordinal))
        {
            // Same given name and same surname, differing in what sits between them — a
            // middle name or an initial the profile does not carry.
            return MatchStrength.Partial;
        }

        // One given name being the start of the other covers the ordinary shortenings and
        // the initial-only listings, in both directions.
        if (seenGiven.StartsWith(knownGiven, StringComparison.Ordinal)
            || knownGiven.StartsWith(seenGiven, StringComparison.Ordinal))
        {
            return MatchStrength.Partial;
        }

        return MatchStrength.Conflicting;
    }

    /// <summary>
    /// What the listing's text does against the addresses on file.
    /// </summary>
    /// <remarks>
    /// Read as one string rather than as parts, because that is how a listing prints one and
    /// splitting somebody else's formatting back into fields is a guess. The street number
    /// and name carry the decision the way a surname does: same street, same town is the same
    /// address; the same town alone is the coincidence that a city of half a million people
    /// makes constantly, and is reported as partial rather than as agreement.
    /// </remarks>
    public static MatchStrength? Addresses(string listing, IReadOnlyList<ProfileAddress> onFile)
    {
        ArgumentNullException.ThrowIfNull(onFile);

        var seen = Normalise(listing);

        if (seen.Length == 0 || onFile.Count == 0)
        {
            return null;
        }

        MatchStrength? best = null;

        foreach (var address in onFile)
        {
            var line1 = Normalise(address.Line1);
            var city = Normalise(address.City);

            var hasStreet = line1.Length > 0 && seen.Contains(line1, StringComparison.Ordinal);
            var hasCity = city.Length > 0 && seen.Contains(city, StringComparison.Ordinal);

            var strength = (hasStreet, hasCity) switch
            {
                (true, true) => MatchStrength.Exact,
                (true, false) => MatchStrength.Partial,
                (false, true) => MatchStrength.Partial,
                _ => MatchStrength.Conflicting,
            };

            best = Stronger(best, strength);
        }

        return best;
    }

    /// <summary>
    /// What the listing's text does against the contact points on file.
    /// </summary>
    /// <remarks>
    /// All or nothing, deliberately. A mailbox or a number is either the same one or it is
    /// somebody else's — there is no partial version of an email address, and inventing one
    /// (a shared domain, a shared area code) would be scoring a coincidence as evidence at
    /// the weight this system gives its most identifying group.
    /// </remarks>
    public static MatchStrength? Contacts(string listing, IReadOnlyList<ProfileContact> onFile)
    {
        ArgumentNullException.ThrowIfNull(onFile);

        var seen = Digits(listing) is { Length: > 0 } digits ? digits : Normalise(listing);

        if (seen.Length == 0 || onFile.Count == 0)
        {
            return null;
        }

        foreach (var contact in onFile)
        {
            var known = contact.Kind is ProfileContactKind.Phone
                ? Digits(contact.Value)
                : Normalise(contact.Value);

            if (known.Length > 0 && string.Equals(seen, known, StringComparison.Ordinal))
            {
                return MatchStrength.Exact;
            }
        }

        return MatchStrength.Conflicting;
    }

    /// <summary>
    /// Case, punctuation and spacing removed, so that only the words are compared.
    /// </summary>
    /// <remarks>
    /// Lower-cased invariantly rather than by the running culture: a Turkish machine
    /// lower-cases I to a dotless one, and a search that matched differently depending on
    /// where the worker happened to be deployed would be a bug nobody could reproduce.
    /// </remarks>
    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var built = new StringBuilder(value.Length);
        var space = false;

        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (space && built.Length > 0)
                {
                    built.Append(' ');
                }

                space = false;
                built.Append(char.ToLowerInvariant(character));

                continue;
            }

            // Every run of anything else — spaces, commas, full stops, non-breaking
            // spaces — collapses to one separator, so "Smith, John" and "Smith  John"
            // compare alike.
            space = true;
        }

        return built.ToString();
    }

    /// <summary>Only the digits, for comparing two spellings of one telephone number.</summary>
    private static string Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var built = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                built.Append(character);
            }
        }

        // A handful of digits is a house number or an age, not a telephone number. Ten is
        // the length of a North American number without its country code, and the shortest
        // thing worth treating as one.
        return built.Length >= 10 ? built.ToString() : string.Empty;
    }

    /// <summary>
    /// The better of two readings of one group.
    /// </summary>
    /// <remarks>
    /// A profile carries several names and several addresses, and a listing agreeing with any
    /// one of them is agreement. Taking the strongest is what stops an old address on file
    /// from turning a match into a contradiction simply by being in the list.
    /// </remarks>
    private static MatchStrength Stronger(MatchStrength? left, MatchStrength right)
    {
        if (left is not { } existing)
        {
            return right;
        }

        return Rank(right) > Rank(existing) ? right : existing;
    }

    private static int Rank(MatchStrength strength) => strength switch
    {
        MatchStrength.Exact => 2,
        MatchStrength.Partial => 1,
        MatchStrength.Conflicting => 0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(strength),
            strength,
            "Unranked match strength. A degree of agreement this cannot order is one that "
            + "would be silently discarded when a profile carries more than one name."),
    };
}
