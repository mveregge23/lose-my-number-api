// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The single definition of how a Dbr <see cref="DbContext"/> talks to Postgres.
/// </summary>
/// <remarks>
/// Deliberately separate from the DI registration so that everything which needs a
/// context configured the same way — the API, the Worker, the schema-drift test in
/// §18.6 (DBR-069), and the Testcontainers harness in §21.2 (DBR-085) — goes through
/// one code path. A test that configures its own context differently from production
/// is a test that can pass while production is broken.
/// </remarks>
public static class DbrDbContextOptionsExtensions
{
    /// <summary>
    /// Points <paramref name="builder"/> at Postgres via Npgsql and applies the
    /// project-wide mapping conventions.
    /// </summary>
    public static DbContextOptionsBuilder UseDbr(
        this DbContextOptionsBuilder builder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder
            .UseNpgsql(connectionString)
            // The schema is hand-written SQL (§18), and that SQL is idiomatic
            // Postgres — lower-case, underscore-separated. Without this, EF would
            // expect PascalCase identifiers and every reference would need to be
            // quoted on both sides. Applying the convention centrally keeps the
            // model and the migrations describing the same names, which is exactly
            // the drift §18.6 says the schema test exists to catch.
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Typed overload, for the callers that build options for one specific context —
    /// test harnesses and the schema-drift runner, mostly.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseDbr<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext
    {
        UseDbr((DbContextOptionsBuilder)builder, connectionString);

        return builder;
    }
}
