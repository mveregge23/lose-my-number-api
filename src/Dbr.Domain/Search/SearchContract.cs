// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Search;

/// <summary>
/// The rules the two sides of a search have to keep, checked where they meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the constructors of the types themselves.</b> A finding is
/// untrusted input twice over: it comes from code or a document somebody else contributed,
/// and that code read a page belonging to a company with no interest in being read. Input
/// like that gets checked once, at the boundary it arrives on, by something that can be
/// tested exhaustively — not by validation scattered across records where a
/// <see langword="with"/> expression copies straight past it and each type can only see
/// its own half of the rule anyway.
/// </para>
/// <para>
/// <b>A sentence rather than a boolean</b>, and never an exception. What follows a broken
/// contract is the caller's decision — this broker's leg of the scan fails while the others
/// carry on, and somebody has to be able to read why from a log — so this says what is
/// wrong and leaves the response to whoever asked.
/// </para>
/// </remarks>
public static class SearchContract
{
    /// <summary>
    /// Why this search must not be given this context, or <see langword="null"/> when it
    /// may be.
    /// </summary>
    /// <remarks>
    /// The interesting rule is the last one: an identity carrying a group the search never
    /// declared means the release handed over more than was asked for, which is a fault in
    /// the release path rather than in the search — and one that has already decrypted
    /// something by the time it is visible here. <b>It catches every over-release that
    /// actually carries data</b>, since a group that arrived empty released nothing to
    /// catch. That is the whole of what this check can honestly claim, and it is enough:
    /// the case worth refusing is a date of birth in the hands of a search that never
    /// mentioned one.
    /// </remarks>
    public static string? Refuse(SearchCapabilities capabilities, SearchContext context)
    {
        if (capabilities.RequiredFields.Count == 0)
        {
            return "This search declares that it needs no part of an identity, so there is "
                + "nothing for it to search for. A search must name at least one field.";
        }

        if (context.ScanId == Guid.Empty)
        {
            return "This context names no scan, so nothing could be correlated with the run "
                + "that asked for it.";
        }

        if (context.Broker.BrokerId == Guid.Empty)
        {
            return "This context names no broker, so a finding could not be filed against a "
                + "catalog entry.";
        }

        if (string.IsNullOrWhiteSpace(context.Broker.Domain))
        {
            return "This context names no domain, so there is no site to look at.";
        }

        if (context.AttemptNumber < 1)
        {
            return $"Attempt numbers start at one, and this context is attempt "
                + $"{context.AttemptNumber}.";
        }

        foreach (var field in Released(context.ReleasedIdentity))
        {
            if (!capabilities.RequiredFields.Contains(field))
            {
                return $"This context carries {Spell(field)}, which this search never "
                    + "declared that it needs. A release wider than the declaration is a "
                    + "fault in whatever built it, not something to work around here.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why this result cannot be believed, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// Every rule here is about a claim the search was not in a position to make, or an
    /// answer that means two things at once. Nothing here judges whether a candidate is
    /// good enough to show anybody — that bar is set elsewhere and deliberately not mixed
    /// in with this, since a result that breaks the contract is a bug to fix and a
    /// candidate below the bar is an ordinary finding nobody needs to see.
    /// </remarks>
    public static string? Refuse(SearchCapabilities capabilities, SearchResult result)
    {
        switch (result)
        {
            case SearchResult.Found found:
                return RefuseFound(capabilities, found);

            case SearchResult.Failed failed when string.IsNullOrWhiteSpace(failed.Detail):
                return $"This search failed with {failed.Reason} and said nothing about what "
                    + "happened, which leaves whoever reads the log with the category and "
                    + "none of the evidence.";

            default:
                return null;
        }
    }

    private static string? RefuseFound(SearchCapabilities capabilities, SearchResult.Found found)
    {
        if (found.Candidates.Count == 0)
        {
            return "This search reported findings and listed none. A broker that holds "
                + "nothing is reported as nothing found, which is an answer; an empty list "
                + "of findings is the same answer wearing the wrong shape.";
        }

        var seen = new HashSet<Uri>();

        foreach (var candidate in found.Candidates)
        {
            var refusal = RefuseCandidate(capabilities, candidate);

            if (refusal is not null)
            {
                return refusal;
            }

            if (!seen.Add(candidate.SourceRef))
            {
                return "Two of these findings point at the same listing. One listing is one "
                    + "candidate — counting it twice would make a single page look like "
                    + "corroboration.";
            }
        }

        return null;
    }

    private static string? RefuseCandidate(SearchCapabilities capabilities, SearchCandidate candidate)
    {
        if (!candidate.SourceRef.IsAbsoluteUri)
        {
            return "A finding points at a relative reference, which names a listing only to "
                + "whatever page it was read from — and that page is gone by the time "
                + "anybody follows it.";
        }

        if (candidate.SourceRef.Scheme != Uri.UriSchemeHttp
            && candidate.SourceRef.Scheme != Uri.UriSchemeHttps)
        {
            return $"A finding points at a {candidate.SourceRef.Scheme} reference. A listing "
                + "is a page on the broker's site, and anything else is a link this system "
                + "would be storing and later handing back without knowing what it is.";
        }

        if (candidate.Matches.Count == 0)
        {
            return "A finding claims a listing and gives no reason to think it is this "
                + "person. Something has to have matched, or there was nothing to report.";
        }

        var fields = new HashSet<IdentityField>();

        foreach (var match in candidate.Matches)
        {
            if (!capabilities.RequiredFields.Contains(match.Field))
            {
                return $"A finding claims a match on {Spell(match.Field)}, which this search "
                    + "was never given. It cannot have recognised what it did not have.";
            }

            if (!fields.Add(match.Field))
            {
                return $"A finding reports {Spell(match.Field)} twice. One group of an "
                    + "identity agrees with a listing to one degree, and two answers for it "
                    + "leave no way to say which one counted.";
            }
        }

        if (!candidate.Matches.Any(match => match.Strength != MatchStrength.Conflicting))
        {
            return "A finding reports only contradictions. A listing that disagreed with "
                + "everything it was compared against is somebody else, not a weak match.";
        }

        return null;
    }

    /// <summary>
    /// The groups this identity actually carries something in.
    /// </summary>
    /// <remarks>
    /// Emptiness is the test, because emptiness is all there is to go on: a profile with no
    /// contacts on file and a release that withheld them arrive here identically. They are
    /// the same thing for this purpose — neither one handed anything over.
    /// </remarks>
    private static IEnumerable<IdentityField> Released(ProfileIdentityFields identity)
    {
        if (identity.Names.Count > 0)
        {
            yield return IdentityField.Names;
        }

        if (identity.Addresses.Count > 0)
        {
            yield return IdentityField.Addresses;
        }

        if (identity.Contacts.Count > 0)
        {
            yield return IdentityField.Contacts;
        }

        if (identity.DateOfBirth is not null)
        {
            yield return IdentityField.DateOfBirth;
        }
    }

    /// <summary>How a field is named in a sentence somebody reads.</summary>
    private static string Spell(IdentityField field) => field switch
    {
        IdentityField.Names => "names",
        IdentityField.Addresses => "addresses",
        IdentityField.Contacts => "contacts",
        IdentityField.DateOfBirth => "a date of birth",
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field,
            "Unmapped identity field. Adding a group to an identity means deciding how a "
            + "release names it as well."),
    };
}
