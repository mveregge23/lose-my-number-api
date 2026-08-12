<!--
SPDX-FileCopyrightText: 2026 Max Veregge
SPDX-License-Identifier: AGPL-3.0-or-later
-->

# Database migrations

Plain SQL, applied by `Dbr.Migrator` (DbUp). EF Core is the runtime O/RM and never
owns the schema — there are no EF migrations here, and the design-time tooling that
would generate them is referenced by no project in the solution. Row-level security
policies, partial indexes, and extensions all end up as raw SQL escape hatches under
EF's migration model anyway, at which point the generated C# wrapper stops paying for
itself and the thing being reviewed may as well be the SQL that will actually run.

## Layout

```
db/migrations/
  core/     operational data — jobs, statuses, catalog, audit
  vault/    the envelope-encrypted store holding personally identifying data
```

Two folders, two DbUp journal tables (`public.schema_versions_core`,
`public.schema_versions_vault`). The vault starts as a schema inside the core
database but is expected to become its own database later; giving each set its own
connection string now means that move is a configuration change to one runner rather
than an untangling of shared history afterwards.

## Naming

```
YYYYMMDD_HHMM__short_description.sql
20260811_2145__create_vault_schema.sql
```

Timestamps rather than sequential integers, so two branches adding a migration merge
cleanly instead of both claiming `0042_`. Scripts run in filename order, which the
timestamp prefix makes chronological. `MigrationFileNameTests` fails the build on a
filename that doesn't match.

## Rules

- **Forward-only.** DbUp has no down-migrations and that is kept, not worked around
  A trustworthy automated rollback is harder to build than the forward migration it
  would undo — a `DROP COLUMN` "down" script cannot un-lose the data. A mistake is
  corrected by a new script, not a reverse gear.
- **Never edit a script that has been applied anywhere.** DbUp journals scripts by
  name; an edited script is simply never re-run, so the change silently reaches no
  database that already ran it. Add a new one.
- **Each script runs in a transaction.** Postgres DDL is transactional, so a failure
  mid-script leaves the schema exactly where it started. `CREATE INDEX CONCURRENTLY`
  is the notable statement that cannot run this way and needs a deliberate exception
  in the runner rather than a quiet removal of the transaction.
- **Reviewed as core code**, at a higher bar than broker recipes get — a bad recipe
  fails one broker's jobs, a bad migration can corrupt the tenant boundary itself.
- **No `pgcrypto` needed for `gen_random_uuid()`.** It has been core Postgres since
  13, and the stack runs 17.

## Adding one

Drop the `.sql` file in `core/` or `vault/`. It is compiled into `Dbr.Migrator` by a
wildcard, so there is nothing to register. `docker compose up` applies it before the
API and Worker start.
