// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;

namespace Dbr.Search;

/// <summary>
/// Turns a company's domain into the address a recipe's query is asked of.
/// </summary>
/// <remarks>
/// <para>
/// One function, and it exists so that the recipe cannot be the thing that decides. A recipe
/// writes a path and a query string and has nowhere to put a host; how a catalog domain
/// becomes an origin is settled once, in a composition root, where it is reviewed as code.
/// </para>
/// <para>
/// It is also the seam a test uses to point the real engine at a recorded company on
/// localhost — which is what §21.4 asks for, and is a much smaller thing to hand a test than
/// a fake HTTP client that would skip everything between the parser and a socket.
/// </para>
/// </remarks>
public delegate Uri SearchOrigin(SearchTarget target);

/// <summary>
/// The one engine every recipe-tier search runs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>One class for the whole catalog, driven by a document per company.</b> §9.1's reasoning
/// applies to searching as much as to removal: a hand-written class per broker does not scale
/// to hundreds, and in an open-source project every one of them is arbitrary code a stranger
/// is asking to run against other people's identities. A recipe is data — it can be linted,
/// diffed and merged at a lighter bar, and the worst a bad one does is fail one company's
/// searches.
/// </para>
/// <para>
/// <b>It reports what it saw and judges none of it.</b> Every candidate carries which groups
/// the listing agreed with and how closely, and no score. What that is worth is decided above
/// this line, against a bar that has to mean the same thing whichever company produced the
/// candidate.
/// </para>
/// <para>
/// <b>Nothing here throws for a broker's behaviour.</b> A refusal, a redesign, a timeout: each
/// is an answer with a reason, because an exception escaping a search is a bug in the search
/// and is treated as one. The distinction is what lets a worker tell "this company is having a
/// bad day" from "this recipe is broken".
/// </para>
/// </remarks>
public sealed class GenericWebSearchConnector(
    SearchRecipe recipe,
    HttpClient client,
    SearchOrigin origin)
    : IBrokerSearch
{
    private static readonly HtmlParser Parser = new();

    public SearchCapabilities Capabilities { get; } = recipe.Capabilities;

    /// <summary>The document this instance runs.</summary>
    public SearchRecipe Recipe { get; } = recipe;

    public async Task<SearchResult> SearchAsync(
        SearchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rendered = Recipe.Query.Render(context.ReleasedIdentity);

        if (rendered.Value is null)
        {
            // The profile has nothing where the query needs something. A configuration fault
            // rather than a runtime one — retrying the same wiring is pointless — and the one
            // failure here that never involves the company at all.
            return new SearchResult.Failed(
                SearchFailureReason.Unsupported,
                $"This search needs {rendered.Missing} and the identity it was given has none.",
                Retryable: false);
        }

        Uri address;

        try
        {
            address = new Uri(origin(context.Broker), rendered.Value);
        }
        catch (UriFormatException exception)
        {
            return new SearchResult.Failed(
                SearchFailureReason.Unsupported,
                $"This recipe's query does not make an address: {exception.Message}",
                Retryable: false);
        }

        HttpResponseMessage response;

        try
        {
            response = await client
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping, which is not this company's answer.
            throw;
        }
        catch (TaskCanceledException)
        {
            // A timeout, which arrives here rather than as an HttpRequestException.
            return new SearchResult.Failed(
                SearchFailureReason.Transient,
                "The request timed out.",
                Retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return new SearchResult.Failed(
                SearchFailureReason.Transient,
                $"The request did not complete: {exception.HttpRequestError}.",
                Retryable: true);
        }

        using (response)
        {
            if (Refusal(response) is { } refused)
            {
                return refused;
            }

            var html = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return Read(html, address, context.ReleasedIdentity);
        }
    }

    /// <summary>
    /// What a status code says on its own, before anything is parsed.
    /// </summary>
    /// <remarks>
    /// Read here rather than in a recipe because these are HTTP's meanings and not one
    /// company's. A recipe that could reinterpret a 429 would be a document deciding how hard
    /// to press a company that has just asked it to stop.
    /// </remarks>
    private static SearchResult? Refusal(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.TooManyRequests => new SearchResult.Failed(
            SearchFailureReason.RateLimited,
            RetryAfter(response) is { } after
                ? $"Throttled, and asked to wait {after.TotalSeconds:0} seconds."
                : "Throttled, with no indication of for how long.",

            // The pacing above decides when, and it is the only thing that can: this instance
            // shares one reputation with the company across every tenant queued behind it.
            Retryable: true),

        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new SearchResult.Failed(
            SearchFailureReason.Blocked,
            $"The company refused to serve this instance ({(int)response.StatusCode}).",
            Retryable: false),

        >= HttpStatusCode.InternalServerError => new SearchResult.Failed(
            SearchFailureReason.Transient,
            $"The company answered {(int)response.StatusCode}.",
            Retryable: true),

        HttpStatusCode.NotFound => new SearchResult.Failed(
            SearchFailureReason.PageShapeChanged,

            // Not transient, and the distinction is the whole point of the case. A search
            // endpoint that has moved answers 404 every time, and retrying burns every
            // attempt while leaving the catalog entry looking flaky rather than stale.
            "The search page is not there any more.",
            Retryable: false),

        _ when !response.IsSuccessStatusCode => new SearchResult.Failed(
            SearchFailureReason.Transient,
            $"The company answered {(int)response.StatusCode}, which this search has no reading of.",
            Retryable: true),

        _ => null,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta;

    /// <summary>
    /// What the page says.
    /// </summary>
    /// <remarks>
    /// The order is the design. A challenge page can carry results-shaped markup and a 200,
    /// so it is asked about first; listings are the ordinary answer; and the marker saying the
    /// company holds nothing is asked about only when there were no listings — which is what
    /// separates "nobody by that name" from "this recipe no longer matches the page", the one
    /// distinction that is expensive in both directions.
    /// </remarks>
    private SearchResult Read(string html, Uri address, ProfileIdentityFields identity)
    {
        using var document = Parser.ParseDocument(html);

        if (Recipe.Blocked is { } blocked && document.QuerySelector(blocked) is not null)
        {
            return new SearchResult.Failed(
                SearchFailureReason.Blocked,
                "A challenge page was served in place of results.",
                Retryable: false);
        }

        var items = document.QuerySelectorAll(Recipe.Item);

        if (items.Length == 0)
        {
            if (document.QuerySelector(Recipe.NoResults) is not null)
            {
                return new SearchResult.NothingFound();
            }

            return new SearchResult.Failed(
                SearchFailureReason.PageShapeChanged,

                // Both selectors, because whoever fixes this needs to know the page matched
                // neither — a page matching one of them is a different and much smaller
                // problem.
                $"The page held nothing matching '{Recipe.Item}' and nothing matching "
                + $"'{Recipe.NoResults}', so it is no longer the page this recipe was written "
                + "against.",
                Retryable: false);
        }

        var candidates = new List<SearchCandidate>();
        var seen = new HashSet<Uri>();

        foreach (var item in items)
        {
            var candidate = Candidate(item, address, identity);

            // A listing this recipe cannot point at, or one that agreed with nothing, is
            // dropped rather than reported. Both would be refused by the contract, and
            // failing the whole leg because one row of a results page was malformed would
            // throw away the rows that were fine.
            if (candidate is null || !seen.Add(candidate.SourceRef))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            // The page had listings and not one of them could be reported. That is the recipe
            // being wrong about the shape of a listing rather than the company holding
            // nothing, and saying "nothing found" here would tell somebody they are not
            // listed on a page that plainly lists people.
            return new SearchResult.Failed(
                SearchFailureReason.PageShapeChanged,
                $"The page held {items.Length} listings and none of them could be read: either "
                + $"'{Recipe.Link}' matched no link, or nothing was left to compare against.",
                Retryable: false);
        }

        return new SearchResult.Found(candidates);
    }

    /// <summary>One listing, or nothing when it cannot honestly be reported.</summary>
    private SearchCandidate? Candidate(IElement item, Uri address, ProfileIdentityFields identity)
    {
        var href = item.QuerySelector(Recipe.Link)?.GetAttribute("href");

        if (string.IsNullOrWhiteSpace(href)
            || !Uri.TryCreate(address, href, out var source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var matches = new List<FieldMatch>();

        foreach (var field in Recipe.Fields)
        {
            var text = item.QuerySelector(field.Selector)?.TextContent;

            if (string.IsNullOrWhiteSpace(text))
            {
                // The listing did not print this group. Absent rather than contradicted: a
                // page that says nothing about an address disagrees with nothing.
                continue;
            }

            if (Compare(field.Field, text, identity) is { } strength)
            {
                matches.Add(new FieldMatch(field.Field, strength));
            }
        }

        return matches.Count > 0 ? new SearchCandidate(source, matches) : null;
    }

    /// <summary>
    /// The released identity is threaded through rather than held on the instance.
    /// </summary>
    /// <remarks>
    /// It would be shorter as a field set at the top of a search, and it must not be: this
    /// class holds a recipe and an HTTP client and nothing about a person, which is what lets
    /// one instance per company be resolved once and used for every tenant's leg. An identity
    /// parked on it would be one attempt's decrypted name visible to the next, and the bug
    /// that produced would be somebody being shown somebody else's findings.
    /// </remarks>
    private static MatchStrength? Compare(
        IdentityField field,
        string text,
        ProfileIdentityFields identity) => field switch
        {
            IdentityField.Names => ListingComparison.Names(text, identity.Names),
            IdentityField.Addresses => ListingComparison.Addresses(text, identity.Addresses),
            IdentityField.Contacts => ListingComparison.Contacts(text, identity.Contacts),
            _ => throw new ArgumentOutOfRangeException(
                nameof(field),
                field,
                "This group has no comparison. A recipe naming one is refused when it is read, "
                + "so reaching here means the two lists have come apart."),
        };
}
