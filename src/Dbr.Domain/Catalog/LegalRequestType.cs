// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>What a regime lets somebody actually demand.</summary>
/// <remarks>
/// One broker and one regime can need more than one of these, which is why it is part
/// of what makes a legal basis unique rather than a property of the regime as a whole:
/// a statute that grants deletion and opt-out of sale grants them on separate terms,
/// frequently with separate deadlines.
/// </remarks>
public enum LegalRequestType
{
    /// <summary>Erase what you hold about me.</summary>
    Delete,

    /// <summary>Stop selling it.</summary>
    OptOutSale,

    /// <summary>Stop using it to target advertising at me.</summary>
    OptOutTargetedAds,
}
