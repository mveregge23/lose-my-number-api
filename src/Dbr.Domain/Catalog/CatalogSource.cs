// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// Whether the shared catalog owns a row, or whoever runs this instance does.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the catalog sync retract content. A regime read wrongly and
/// corrected should stop governing requests when its file goes, and a sync that could
/// only add would leave the bad row in place on every install until somebody deleted it
/// by hand.
/// </para>
/// <para>
/// The other half is what it must never touch. An operator may keep their own reading of
/// a regime, or one for a jurisdiction the shared catalog has not reached — and a sync
/// treating its files as the whole truth would delete that on the next deploy.
/// <see cref="Local"/> is the default for exactly that reason: a row arriving by any
/// route other than the sync is somebody's own until they say otherwise.
/// </para>
/// </remarks>
public enum CatalogSource
{
    /// <summary>This instance's own, and never modified or removed by the sync.</summary>
    Local,

    /// <summary>The shared catalog's, to insert, update and remove from its files.</summary>
    Catalog,
}
