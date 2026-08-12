// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;

namespace Dbr.Migrator;

/// <summary>
/// One of the two migration sets of §18.4 — the core store and the vault store.
/// </summary>
/// <remarks>
/// They are separate because §4 describes the vault as a store that "could start as a
/// separate schema, promoted to a separate database/service later without an API
/// shape change". Each set therefore has its own connection string and its own DbUp
/// journal table, so promoting the vault later is a connection-string change to one
/// set rather than an exercise in untangling a shared migration history.
/// </remarks>
/// <param name="Name">Set name, and the prefix its embedded scripts carry.</param>
/// <param name="ConnectionStringVariable">Environment variable holding its connection string.</param>
/// <param name="JournalTable">DbUp's record of which scripts have run against this store.</param>
public sealed record MigrationSet(string Name, string ConnectionStringVariable, string JournalTable)
{
    /// <summary>
    /// Operational data: jobs, statuses, catalog, audit. Everything outside the vault.
    /// </summary>
    public static readonly MigrationSet Core =
        new("core", "ConnectionStrings__Core", "schema_versions_core");

    /// <summary>
    /// The envelope-encrypted PII store (§1, §4). A schema today, its own database
    /// later; either way it keeps its own journal.
    /// </summary>
    public static readonly MigrationSet Vault =
        new("vault", "ConnectionStrings__Vault", "schema_versions_vault");

    /// <summary>
    /// Applied in this order. Core first: it owns the shared schema-level objects a
    /// vault script may reasonably expect to already exist.
    /// </summary>
    public static IReadOnlyList<MigrationSet> All { get; } = [Core, Vault];

    /// <summary>
    /// Resource-name prefix distinguishing this set's scripts from the other's.
    /// </summary>
    public string ResourcePrefix => $"{Name}.";

    /// <summary>
    /// This set's scripts, in the order they will be applied. Filenames are
    /// timestamp-prefixed (§18.4), so ordinal name order is chronological order.
    /// </summary>
    public IReadOnlyList<string> ScriptNames(Assembly assembly) =>
        [.. assembly.GetManifestResourceNames()
            .Where(Owns)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Whether an embedded resource belongs to this set.
    /// </summary>
    public bool Owns(string resourceName) =>
        resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal);
}
