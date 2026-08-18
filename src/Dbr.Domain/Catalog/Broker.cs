// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// One data broker, as the catalog knows it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This belongs to nobody.</b> It is the first entity here that is not tenant-scoped
/// and deliberately does not implement the interface that would scope it: a broker is a
/// company, and there is no account for a policy to compare against. Every tenant reads
/// the same rows, which is the point — the catalog is what makes one person's removal
/// benefit from what was learned doing somebody else's.
/// </para>
/// <para>
/// <b>Read-only from the application's side.</b> The role serving requests holds
/// <c>SELECT</c> and nothing more. Curated content arrives by a reviewed path, so the
/// code that works out a statutory deadline cannot edit the statute it worked from.
/// </para>
/// <para>
/// The pacing fields are here rather than in configuration because they are facts about
/// one company. Every tenant's job for a broker shares a single lane, so the numbers
/// describe how that company is willing to be spoken to, not how busy this instance is.
/// </para>
/// </remarks>
public class Broker
{
    public Guid Id { get; init; }

    /// <summary>The company, as somebody would recognise it.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The site a listing is found on, and the catalog's real identity for a company.
    /// </summary>
    /// <remarks>
    /// Unique, because two rows for one broker would pace it as two lanes and let the
    /// same person be submitted twice.
    /// </remarks>
    public required string Domain { get; set; }

    public required RemovalMethod RemovalMethod { get; set; }

    /// <summary>
    /// How long a removal is given when no statute governs it.
    /// </summary>
    /// <remarks>
    /// A courtesy target rather than a guarantee. Which of the two a request received is
    /// recorded on the request itself, so that a date somebody is shown can be honest
    /// about which kind it is.
    /// </remarks>
    public required int SlaDays { get; set; }

    /// <summary>Whether this entry is dispatched against at all.</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// When something last confirmed this entry against the live site, or
    /// <see langword="null"/> if nothing ever has.
    /// </summary>
    /// <remarks>
    /// Null and long-ago are different problems: one is an entry nobody has checked,
    /// the other is an entry that has quietly stopped working. Defaulting this to the
    /// moment of insert would turn the first into the second.
    /// </remarks>
    public DateTimeOffset? CatalogVerifiedAt { get; set; }

    /// <summary>How many jobs for this broker may run at once, across every tenant.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>The least time between two jobs in this broker's lane.</summary>
    public int MinDelayMs { get; set; } = 1000;

    /// <summary>Consecutive rate-limited answers that open the breaker.</summary>
    public int RateLimitThreshold { get; set; } = 3;

    /// <summary>How long the breaker stays open before one trial job is allowed.</summary>
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>Consecutive "the form changed" answers that flag this entry for review.</summary>
    public int FormChangeThreshold { get; set; } = 3;

    /// <summary>Which address this broker will correspond at.</summary>
    public EmailContactMode EmailContactMode { get; set; } = EmailContactMode.AliasPreferred;

    public DateTimeOffset CreatedAt { get; init; }
}
