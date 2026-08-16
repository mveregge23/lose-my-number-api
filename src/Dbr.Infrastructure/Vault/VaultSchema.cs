// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Where the vault store lives inside the database it currently shares with the core
/// store.
/// </summary>
/// <remarks>
/// Named once so that the day the vault becomes a database of its own — the move its
/// separate connection string and migration journal exist to allow — there is one
/// place to stop qualifying, rather than a spelling of "vault" scattered across every
/// mapping.
/// </remarks>
public static class VaultSchema
{
    public const string Name = "vault";
}
