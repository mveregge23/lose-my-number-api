// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Search;

/// <summary>
/// Turns what a listing agreed with into one number, and says where the bar is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Above the search line, and one implementation for every broker.</b> A search reports
/// which groups of an identity a listing appeared to agree with and how closely, and no
/// score — because a bar that decides whether somebody is shown their own data has to mean
/// the same thing whichever broker produced the candidate, and it cannot if four hundred
/// separately contributed searches each invent a number for it. This is that one meaning.
/// </para>
/// <para>
/// <b>The source URL is deliberately not an input.</b> A broker's profile link routinely
/// contains the name and the city of the person it is about, so scoring it would count the
/// same agreement twice — once because the search matched the name on the page, and again
/// because the name is in the address bar. Confidence is a function of the matches and
/// nothing else, which is what the signature here says.
/// </para>
/// <para>
/// <b>It assumes a candidate that has already passed
/// <see cref="SearchContract.Refuse(SearchCapabilities, SearchResult)"/>.</b> That is where
/// a finding claiming a field it was never given, or reporting one group twice, is caught.
/// Re-checking it here would put the same rule in two places and let them disagree; what
/// this does instead is arithmetic on input somebody else has already vouched for.
/// </para>
/// </remarks>
public static class MatchConfidence
{
    /// <summary>
    /// The least confidence at which a finding is somebody's business.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set at the point that separates one agreement from two. A listing that agrees on a
    /// name and nothing else scores 0.25 and does not clear this — which is the case the
    /// bar exists for, because a people-search site is full of other people with the same
    /// name and surfacing every one of them would train somebody to dismiss findings
    /// without reading them. Add any second agreement, even a partial one, and it clears.
    /// </para>
    /// <para>
    /// <b>Below this nothing is written at all</b>, rather than written and hidden. An
    /// exposure row is a durable record that a company probably holds this person's data,
    /// and with the source reference it will carry it is a further copy of their identity —
    /// so keeping rows nobody will ever be shown means retaining more of somebody than the
    /// service does anything with, which is the opposite of what it is for. What is kept
    /// instead is the count of candidates that fell below, logged against the broker: a
    /// number is enough to notice a bar set wrong, and it is not about anybody.
    /// </para>
    /// </remarks>
    public const double Floor = 0.35;

    /// <summary>
    /// Net corroboration at which confidence reaches one half.
    /// </summary>
    /// <remarks>
    /// The anchor the whole curve is set by, and it is one concrete finding: a name and a
    /// street address that both agree exactly, which is the ordinary "this is probably
    /// them" result on a people-search site and deserves to read as an even bet rather
    /// than as a near certainty.
    /// </remarks>
    private const double HalfwayEvidence = 3.0;

    /// <summary>
    /// How much one group agreeing is worth, relative to the others.
    /// </summary>
    /// <remarks>
    /// Ordered by how many people a group rules out. A contact point is nearly an
    /// identifier on its own — one mailbox, one person — while a name rules out almost
    /// nobody, which is exactly why a name-only match is the finding this whole bar exists
    /// to hold back. An address sits between them: shared by a household and by whoever
    /// lived there before, but not by strangers. A date of birth rules out a great many
    /// people and still leaves thousands, so it corroborates well and identifies badly.
    /// </remarks>
    private static double WeightOf(IdentityField field) => field switch
    {
        IdentityField.Contacts => 3.0,
        IdentityField.Addresses => 2.0,
        IdentityField.DateOfBirth => 1.5,
        IdentityField.Names => 1.0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field,
            "Unweighted identity field. Adding a group to an identity means deciding how "
            + "much a listing agreeing with it is worth, because a group with no weight "
            + "would be evidence this silently ignores."),
    };

    /// <summary>
    /// How much of a group's weight this degree of agreement earns, or costs.
    /// </summary>
    /// <remarks>
    /// <b>The asymmetry is the point.</b> Agreement is weak evidence and disagreement is
    /// strong: two people sharing a surname is a coincidence that happens constantly, while
    /// a listing showing a different date of birth is somebody else and says so plainly. So
    /// a partial agreement earns half its group's weight and a contradiction costs all of
    /// it, and a finding that agrees on a name while disagreeing on an address ends up
    /// worth less than nothing — which is the correct reading of it.
    /// </remarks>
    private static double FactorOf(MatchStrength strength) => strength switch
    {
        MatchStrength.Exact => 1.0,
        MatchStrength.Partial => 0.5,
        MatchStrength.Conflicting => -1.0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(strength),
            strength,
            "Unscored match strength. A degree of agreement this does not price is one a "
            + "search can report and nothing can weigh."),
    };

    /// <summary>
    /// How sure it is that this listing is this person, from 0 to just under 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It never reaches 1.</b> The evidence saturates rather than accumulating to
    /// certainty, because a search compared some text on a page against some text in a
    /// profile and no amount of that adds up to knowing. A candidate is an observation, and
    /// a score of exactly 1 would be this system claiming otherwise on a page it read once.
    /// </para>
    /// <para>
    /// Contradictions are netted off before the curve is applied, and the total is floored
    /// at zero: a listing that disagreed with more than it agreed with is no evidence at
    /// all rather than negative evidence, since there is nothing below "this is not them"
    /// for a score to express.
    /// </para>
    /// </remarks>
    /// <param name="matches">
    /// What each group of the identity did. An empty list scores zero — no evidence is no
    /// confidence — which then fails <see cref="ClearsFloor"/>, so a caller that somehow
    /// reaches here with nothing shows nobody anything.
    /// </param>
    public static double Score(IReadOnlyList<FieldMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var evidence = 0.0;

        foreach (var match in matches)
        {
            evidence += WeightOf(match.Field) * FactorOf(match.Strength);
        }

        if (evidence <= 0.0)
        {
            return 0.0;
        }

        return evidence / (evidence + HalfwayEvidence);
    }

    /// <summary>Whether a score is high enough to be worth somebody's attention.</summary>
    /// <remarks>
    /// A method rather than a comparison written out at each call site. The bar is one
    /// decision and it moves; a <c>&gt;=</c> against a constant is the kind of thing that
    /// gets copied to a second place and then only fixed in the first.
    /// </remarks>
    public static bool ClearsFloor(double confidence) => confidence >= Floor;
}
