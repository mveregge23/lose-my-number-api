// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Tests.Persistence;

/// <summary>
/// The naming rule both stores are held to, in one place so that adding a context does
/// not quietly add a model nothing checks.
/// </summary>
public static partial class ModelConventions
{
    /// <summary>
    /// Every entity whose table is not the snake_cased name of its type. Empty is the
    /// healthy answer.
    /// </summary>
    public static IReadOnlyList<string> TableNameMismatches(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. context.Model.GetEntityTypes()
                .Where(entityType => entityType.GetTableName() is not null && !entityType.IsOwned())
                .Select(entityType => new
                {
                    Entity = entityType.ClrType.Name,
                    Actual = entityType.GetTableName(),
                    Expected = ToSnakeCase(entityType.ClrType.Name),
                })
                .Where(mapping => mapping.Actual != mapping.Expected)
                .Select(mapping => $"{mapping.Entity} -> '{mapping.Actual}', expected '{mapping.Expected}'"),
        ];
    }

    /// <summary>
    /// Mirrors what the snake_case naming convention does, so the assertion states the
    /// expectation independently rather than asking the mapping to agree with itself.
    /// </summary>
    private static string ToSnakeCase(string name) =>
        WordBoundary().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
