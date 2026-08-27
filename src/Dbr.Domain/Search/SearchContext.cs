// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Search;

/// <summary>
/// The company being searched, in the only terms a search needs it.
/// </summary>
/// <remarks>
/// A few fields rather than the catalog row. Most of what the catalog holds about a
/// broker is about how this instance treats it — how fast its lane runs, when the breaker
/// opens, how long a removal is given — and none of that is a search's business; a search
/// that could read its own pacing would be a search that could argue with it. What is left
/// is what a search actually needs: which site, and which id to report against.
/// </remarks>
/// <param name="BrokerId">
/// The catalog's identity for the company. Carried so a finding can be filed against the
/// right row even after a domain is corrected, which is the same reason lanes are named by
/// id rather than by domain.
/// </param>
/// <param name="Domain">The site to look on.</param>
public sealed record SearchTarget(Guid BrokerId, string Domain);

/// <summary>
/// Everything one search of one broker gets.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ReleasedIdentity"/> is a copy, not a handle.</b> It holds the values the
/// vault released for this attempt and there is no way from here to ask for more — a
/// search that turns out to need a date of birth fails rather than fetches one, and the
/// fix is a review of what it declares rather than a decision it makes at runtime. The
/// groups it does not carry are simply absent; a group present but empty means the profile
/// has none on file, which is an answer rather than a refusal.
/// </para>
/// <para>
/// The identity type is the profile's own rather than a second one shaped like it. A
/// parallel record would have to be kept in step with the four groups the vault encrypts,
/// and it would arrive without the one property that matters most here: this type refuses
/// to print its own contents, so a search that logs its context logs how many names it was
/// given rather than what they are.
/// </para>
/// <para>
/// There is no tenant on this row and no profile id, deliberately. A search has no
/// legitimate use for either, and a field that exists is a field that ends up in a log
/// line, a queue message, or a broker's server logs by way of a query string somebody
/// built out of "everything we know".
/// </para>
/// </remarks>
/// <param name="ScanId">
/// The run this search belongs to. It is here as the thing an attempt is correlated by —
/// a search invoked twice for one scan and broker is the same question asked twice, not
/// two questions.
/// </param>
/// <param name="AttemptNumber">
/// Which try this is, from one. A search that wants to behave differently on a retry can;
/// most will not, and the number is here so that the ones that do are not left inferring
/// it from state they would have to keep between attempts.
/// </param>
public sealed record SearchContext(
    Guid ScanId,
    SearchTarget Broker,
    ProfileIdentityFields ReleasedIdentity,
    int AttemptNumber);
