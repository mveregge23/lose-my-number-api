// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Api.Endpoints;

/// <summary>
/// The catalog, read by anybody: which brokers are known, and which statutes have been
/// confirmed to govern them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the only routes here that require no token</b>, and that is the whole
/// character of them. Nothing behind them belongs to an account — a broker is a company
/// and a legal basis is a statute — so there is nobody to scope the answer to, and asking
/// for a token would mean somebody has to open an account to find out whether this
/// service could help them at all. A caller with no token establishes no tenant, which
/// for every other table in this schema is the case that returns nothing; these tables
/// carry no policy, so it is the case that returns the catalog.
/// </para>
/// <para>
/// <b>The pacing fields are not on the wire.</b> How many jobs a broker's lane runs at
/// once, how long between them, how many rate-limited answers open the breaker and how
/// long it stays open are all in the catalog row, and none of them are published. They
/// are not facts about the company somebody is asking about — they are this instance's
/// tuning for talking to it, and the exact number of refusals that stops it from trying
/// is the sort of thing that is only useful to whoever wants the trying to stop.
/// </para>
/// <para>
/// <b>Everything else is a public fact.</b> A broker's name, domain, opt-out method and
/// courtesy target, and a statute's deadline, citation and reviewer, are things anybody
/// could look up; publishing them is what lets somebody check this instance's homework
/// before trusting a deadline it quotes them.
/// </para>
/// </remarks>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // No RequireAuthorization, unlike every other group. See the note above: the
        // absence is the design, not an omission.
        var brokers = endpoints.MapGroup("/api/v1/brokers");

        brokers.MapGet("/", ListBrokersAsync);
        brokers.MapGet("/{brokerId:guid}", FindBrokerAsync);

        var legalBases = endpoints.MapGroup("/api/v1/legal-basis");

        legalBases.MapGet("/", ListLegalBasesAsync);
        legalBases.MapGet("/{legalBasisId:guid}", FindLegalBasisAsync);

        return endpoints;
    }

    /// <summary>
    /// The brokers this instance knows about and is willing to dispatch against.
    /// </summary>
    /// <remarks>
    /// The whole matching set, in one answer. The catalog is small, identical for every
    /// caller and cacheable as a result, so paging it would be machinery for a problem
    /// this does not have yet — and the answer when it does is one cached response rather
    /// than a page cursor per client. The wrapper object is here so that adding one later
    /// is an added field rather than a changed shape.
    /// </remarks>
    private static async Task<IResult> ListBrokersAsync(
        string? removalMethod,
        string? legalBasisId,
        ICatalogService catalog,
        CancellationToken cancellationToken)
    {
        var parsed = CatalogFilters.ParseBrokerFilter(removalMethod, legalBasisId);

        if (parsed.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var brokers = await catalog.ListBrokersAsync(parsed.Filter, cancellationToken);

        return Results.Ok(new { brokers = brokers.Select(Summary) });
    }

    /// <summary>One broker, and the regimes somebody has confirmed reach it.</summary>
    /// <remarks>
    /// A deactivated entry answers here rather than 404-ing, and says so. Somebody
    /// holding a link to a broker this instance has stopped dispatching against is
    /// better served by "this exists and is not being worked" than by an answer that
    /// reads as though the company was never in the catalog.
    /// </remarks>
    private static async Task<IResult> FindBrokerAsync(
        Guid brokerId,
        ICatalogService catalog,
        CancellationToken cancellationToken)
    {
        var entry = await catalog.FindBrokerAsync(brokerId, cancellationToken);

        return entry is null ? NoSuchBroker() : Results.Ok(Detail(entry));
    }

    /// <summary>The regimes this instance can act under.</summary>
    private static async Task<IResult> ListLegalBasesAsync(
        string? residencyScope,
        string? requestType,
        ICatalogService catalog,
        CancellationToken cancellationToken)
    {
        var parsed = CatalogFilters.ParseLegalBasisFilter(residencyScope, requestType);

        if (parsed.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var bases = await catalog.ListLegalBasesAsync(parsed.Filter, cancellationToken);

        return Results.Ok(new { legalBases = bases.Select(Regime) });
    }

    private static async Task<IResult> FindLegalBasisAsync(
        Guid legalBasisId,
        ICatalogService catalog,
        CancellationToken cancellationToken)
    {
        var basis = await catalog.FindLegalBasisAsync(legalBasisId, cancellationToken);

        return basis is null
            ? Results.Problem(
                "No such legal basis. The catalog is curated, so a regime nobody has reviewed and "
                + "entered is absent rather than incomplete.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(Regime(basis));
    }

    private static object Summary(Broker broker) =>
        new
        {
            id = broker.Id,
            name = broker.Name,
            domain = broker.Domain,
            removalMethod = CatalogVocabulary.ToWire(broker.RemovalMethod),

            // Labelled for what it is. This is the broker's own courtesy target, and a
            // request governed by a statute takes its deadline from the regime instead —
            // presenting the two under one name is how somebody comes to believe they
            // have a legal deadline they do not have.
            operationalSlaDays = broker.SlaDays,

            emailContactMode = CatalogVocabulary.ToWire(broker.EmailContactMode),

            // Null when nothing has ever checked this entry against the live site, which
            // is a different thing from checked long ago and worth being able to tell
            // apart from the outside.
            catalogVerifiedAt = broker.CatalogVerifiedAt,
        };

    private static object Detail(BrokerEntry entry) =>
        new
        {
            id = entry.Broker.Id,
            name = entry.Broker.Name,
            domain = entry.Broker.Domain,
            removalMethod = CatalogVocabulary.ToWire(entry.Broker.RemovalMethod),
            operationalSlaDays = entry.Broker.SlaDays,
            emailContactMode = CatalogVocabulary.ToWire(entry.Broker.EmailContactMode),
            catalogVerifiedAt = entry.Broker.CatalogVerifiedAt,

            // Only on the detail. The listing is active entries alone, so a field there
            // would be a constant somebody would reasonably try to filter on.
            active = entry.Broker.Active,

            // Empty is a real answer: nobody has confirmed a statute reaches this
            // company, so a removal against it gets the courtesy target above. It does
            // not mean no statute does.
            legalBases = entry.Regimes.Select(Confirmed),
        };

    private static object Confirmed(ConfirmedRegime regime) =>
        new
        {
            id = regime.Basis.Id,
            code = regime.Basis.Code,
            requestType = CatalogVocabulary.ToWire(regime.Basis.RequestType),
            residencyScope = regime.Basis.ResidencyScope,
            responseDeadlineDays = regime.Basis.ResponseDeadlineDays,
            extensionDays = regime.Basis.ExtensionDays,
            deadlineUnit = CatalogVocabulary.ToWire(regime.Basis.DeadlineUnit),
            verificationLevel = CatalogVocabulary.ToWire(regime.Basis.VerificationLevel),
            citationUrl = regime.Basis.CitationUrl,
            reviewedAt = regime.Basis.ReviewedAt,
            reviewedBy = regime.Basis.ReviewedBy,

            // Who decided this statute reaches this company, and when. The part no code
            // could work out, which is why it is published rather than summarised away.
            confirmedAt = regime.ConfirmedAt,
            confirmedBy = regime.ConfirmedBy,
        };

    private static object Regime(LegalBasis basis) =>
        new
        {
            id = basis.Id,
            code = basis.Code,
            requestType = CatalogVocabulary.ToWire(basis.RequestType),
            residencyScope = basis.ResidencyScope,
            responseDeadlineDays = basis.ResponseDeadlineDays,
            extensionDays = basis.ExtensionDays,

            // Days alone are not a duration here. Fifteen business days and fifteen
            // calendar days are most of a week apart, so a client rendering the number
            // without the unit would misstate the deadline in exactly the direction
            // that tells somebody they have recourse before they do.
            deadlineUnit = CatalogVocabulary.ToWire(basis.DeadlineUnit),

            verificationLevel = CatalogVocabulary.ToWire(basis.VerificationLevel),

            // Provenance travels with the row wherever it goes. A deadline somebody is
            // shown should come with the source it was read from and the name of whoever
            // read it, so it can be checked rather than taken on faith.
            citationUrl = basis.CitationUrl,
            reviewedAt = basis.ReviewedAt,
            reviewedBy = basis.ReviewedBy,
        };

    private static IResult NoSuchBroker() =>
        Results.Problem(
            "No such broker in this instance's catalog.",
            statusCode: StatusCodes.Status404NotFound);
}
