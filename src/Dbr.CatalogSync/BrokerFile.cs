// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.CatalogSync;

/// <summary>
/// One company's file, as it is written.
/// </summary>
/// <remarks>
/// <para>
/// Sits beside the company's recipes as <c>broker.yaml</c>, and is the row those recipes
/// bind to: a <c>search.yaml</c> or <c>email.yaml</c> names a broker by id, and this is
/// where that id becomes a company with a domain. The three are reviewed together because
/// they only make sense together — a recipe for a company nobody has described never runs,
/// and a company with a mailbox method and no mailbox document is a demand that stays queued.
/// </para>
/// <para>
/// Lighter than a jurisdiction's file on purpose. A statute row decides what somebody is
/// told about their legal position and carries a citation and a reviewer for that reason;
/// a company row is public fact about a company, reviewed at the one-approval bar, and the
/// pull request that landed it is its provenance. What it does carry is where the facts
/// were read, so a reviewer can go and look.
/// </para>
/// </remarks>
public sealed class BrokerFile
{
    /// <summary>
    /// The company's identity for the life of the catalog, and what its recipes bind to.
    /// </summary>
    /// <remarks>
    /// Assigned in the file rather than by the database, because the recipes beside it
    /// name it and a lane is named after it. A domain can be corrected — companies rename
    /// — and everything that points at the company keeps pointing at it when that happens.
    /// </remarks>
    public Guid? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>
    /// The bare host a listing is found under: <c>example.com</c>, never a URL.
    /// </summary>
    /// <remarks>
    /// This is the one place a company's address is written. A search recipe writes a path
    /// and a mailbox recipe writes a local part, and both are completed from here, which is
    /// what stops a reviewed document from sending somebody's name anywhere else.
    /// </remarks>
    public string? Domain { get; set; }

    /// <summary><c>webform</c>, <c>email</c>, <c>api</c> or <c>postal</c>.</summary>
    public string? RemovalMethod { get; set; }

    /// <summary>The courtesy target when no statute governs a request.</summary>
    public int? SlaDays { get; set; }

    /// <summary><c>alias_preferred</c> or <c>tenant_real_required</c>. Optional; alias by default.</summary>
    public string? EmailContactMode { get; set; }

    /// <summary>
    /// Whether the company is searched, paced and sent to. Optional; true by default.
    /// </summary>
    /// <remarks>
    /// A file saying <c>false</c> is the catalog's statement that the company has closed or
    /// merged: the row stays, because history points at it, and nothing new is sent.
    /// </remarks>
    public bool? Active { get; set; }

    /// <summary>
    /// How gently to talk to this company. Optional as a block and per field: anything
    /// left out takes the slowest lane there is, which is the right default for a row
    /// added without thinking about it.
    /// </summary>
    public BrokerPacing? Pacing { get; set; }

    /// <summary>
    /// Where the domain, method and target were read — the company's privacy page, or a
    /// registry entry. Required, not stored: the file's history is the record, and this
    /// is what a reviewer follows to check it.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The regimes that reach this company, each with the evidence for saying so.
    /// </summary>
    /// <remarks>
    /// The judgement the design says no code may make, written where a reviewer can
    /// check it. Optional as a list: a company with none falls back to its courtesy
    /// target on every request, honestly labelled as such, which is the safe absence.
    /// </remarks>
    public List<SubjectToEntry> SubjectTo { get; set; } = [];
}

/// <summary>
/// One regime that reaches the company, and why that is believed.
/// </summary>
/// <remarks>
/// Named by regime code rather than by request type, because applicability is a fact
/// about the statute and the company: a regime that reaches a company reaches it for
/// every kind of demand it grants. The sync writes one confirmation per row the regime
/// has. This carries what the regime file carries and the company row does not — a
/// citation and a reviewer — because it is a legal claim rather than a public fact.
/// </remarks>
public sealed class SubjectToEntry
{
    /// <summary>The code of a regime in <c>catalog/legal-basis/</c>.</summary>
    public string? Regime { get; set; }

    /// <summary>
    /// Where it was read that the regime reaches this company. A state's data-broker
    /// registry is the usual source: a company on it has declared itself subject to
    /// that state's law.
    /// </summary>
    public string? EvidenceUrl { get; set; }

    public string? ConfirmedBy { get; set; }

    /// <summary>A date, read as midnight UTC, for the reason the regime file gives.</summary>
    public DateTime? ConfirmedAt { get; set; }
}

/// <summary>The pacing block, mirroring the columns it fills.</summary>
public sealed class BrokerPacing
{
    public int? MaxConcurrency { get; set; }

    public int? MinDelayMs { get; set; }

    public int? RateLimitThreshold { get; set; }

    public int? CooldownMinutes { get; set; }

    public int? FormChangeThreshold { get; set; }
}

/// <summary>
/// The one line read from a recipe: which company it is for.
/// </summary>
/// <remarks>
/// The recipes themselves are read and validated by the engines that run them. What only
/// the catalog can check is that the company they name is the company beside them.
/// </remarks>
public sealed class RecipeHeader
{
    public Guid? BrokerId { get; set; }
}
