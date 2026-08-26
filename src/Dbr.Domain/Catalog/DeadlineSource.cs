// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Where a deadline came from: a statute, or the broker's own target.
/// </summary>
/// <remarks>
/// <para>
/// The whole reason this is recorded rather than collapsed into the date is that the two
/// are different promises. An operational default slipping is disappointing. A statutory
/// deadline slipping is something the person may have actual recourse over, and they
/// cannot tell which they are looking at unless the answer travels with the date.
/// </para>
/// <para>
/// There is no third value for "nobody has confirmed a statute reaches this company yet".
/// That case is <see cref="OperationalDefault"/> too, and deliberately: what a person
/// gets is the broker's courtesy target either way, and inventing a middle state would
/// offer a distinction that changes nothing about what they can expect.
/// </para>
/// <para>
/// Its spelling was held back until something stored it, on the grounds that pinning one
/// before a column and a wire format existed would be a guess about two things at once.
/// <c>RemovalRequest</c> is that something, so <see cref="CatalogVocabulary"/> now carries
/// it like every other catalog enum.
/// </para>
/// </remarks>
public enum DeadlineSource
{
    /// <summary>A regime confirmed to govern this broker for this kind of request.</summary>
    Statutory,

    /// <summary>The broker's own target, which is a courtesy rather than an obligation.</summary>
    OperationalDefault,
}
