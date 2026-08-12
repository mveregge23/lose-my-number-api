// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// Every database test shares one container. Starting Postgres and migrating it takes
/// seconds; doing that per test class would dominate the suite's runtime for no
/// isolation benefit, since tests scope themselves with their own table names.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Separate from the Postgres collection so that a test needing only a database never
/// pays to start OpenBao, and vice versa.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OpenBaoCollection : ICollectionFixture<OpenBaoFixture>
{
    public const string Name = "openbao";
}
