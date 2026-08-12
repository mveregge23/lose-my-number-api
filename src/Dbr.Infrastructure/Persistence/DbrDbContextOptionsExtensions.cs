// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The single definition of how a Dbr <see cref="DbContext"/> talks to Postgres.
/// </summary>
/// <remarks>
/// Deliberately separate from the DI registration so that everything needing a
/// context configured the same way — the API, the Worker, and the integration tests
/// that run against a real Postgres — goes through one code path. A test that
/// configures its own context differently from production is a test that can pass
/// while production is broken.
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
            // The schema is hand-written SQL, and that SQL is idiomatic Postgres:
            // lower-case, underscore-separated. Without this, EF would expect
            // PascalCase identifiers and every reference would have to be quoted on
            // both sides. Applying the convention centrally is what keeps the model
            // and the migrations describing the same names.
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Typed overload, for callers that build options for one specific context —
    /// test harnesses, mostly.
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
