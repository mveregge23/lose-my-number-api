# lose-my-number-api

A public service to request the removal of personal data from data broker sites, built with privacy in mind.

Open source and self-hostable end to end: `docker compose up` runs the whole thing with no cloud
account and no paid dependency. The maintainer also runs a hosted instance; both come from this
same codebase, and the differences are spelled out in [Deployment modes](#deployment-modes) below.

> **Status: early.** The solution scaffold and local infrastructure exist; the application itself is
> being built out story by story. See the implementation backlog for what's done and what's next.

---

## Contents

- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [What's running](#whats-running)
- [Reaching the services from your machine](#reaching-the-services-from-your-machine)
- [Data persistence](#data-persistence)
- [Database migrations](#database-migrations)
- [The tenant boundary](#the-tenant-boundary)
- [The vault: where identifying data lives](#the-vault-where-identifying-data-lives)
- [What the account permits](#what-the-account-permits)
- [The broker catalog, readable by anybody](#the-broker-catalog-readable-by-anybody)
- [Asking for a scan](#asking-for-a-scan)
- [What the scans found](#what-the-scans-found)
- [Monthly scans, and the day yours falls on](#monthly-scans-and-the-day-yours-falls-on)
- [Signing in: passkeys](#signing-in-passkeys)
- [Sessions and tokens](#sessions-and-tokens)
- [OpenBao, sealing, and the unseal key](#openbao-sealing-and-the-unseal-key)
- [Logs, and what cannot get into them](#logs-and-what-cannot-get-into-them)
- [Working on the code without Docker](#working-on-the-code-without-docker)
- [Deployment modes](#deployment-modes)
- [Security: local defaults vs. a real deployment](#security-local-defaults-vs-a-real-deployment)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)

---

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| Docker | 29.x + Compose v2 | Docker Desktop on macOS/Windows, or Docker Engine on Linux |
| .NET SDK | 10.0 | Only needed to build/test outside containers |

The integration test suite also needs Docker, since it starts its own throwaway Postgres and
OpenBao containers — see [Working on the code without Docker](#working-on-the-code-without-docker).

Nothing else. No local Postgres, RabbitMQ, or key-management service is required — the compose
stack brings its own.

## Quick start

```bash
git clone https://github.com/mveregge23/lose-my-number-api.git
cd lose-my-number-api
docker compose up
```

That's the whole setup. On first run it builds the API and Worker images, initializes the OpenBao
barrel, and enables the Transit secrets engine. Then:

```bash
curl http://localhost:8080/
```

To stop, `Ctrl+C` (or `docker compose down` if you started with `-d`). **Your data survives** —
see [Data persistence](#data-persistence).

### Configuration

Every setting has a working development default, so there is no required setup step. To override
anything, copy the example file and edit it:

```bash
cp .env.example .env
```

`.env` is gitignored. The defaults are development-only credentials — see
[Security](#security-local-defaults-vs-a-real-deployment) before running this anywhere but a
laptop.

## What's running

| Service | Image | Purpose |
|---|---|---|
| `api` | built from `src/Dbr.Api` | ASP.NET Core minimal API |
| `worker` | built from `src/Dbr.Worker` | Background job processing |
| `postgres` | `postgres:17-alpine` | Operational data, tenant-scoped via row-level security |
| `rabbitmq` | `rabbitmq:4-management-alpine` | Job queue |
| `openbao` | `openbao/openbao:2` | Key management — per-tenant envelope encryption |
| `openbao-init` | `openbao/openbao:2` | One-shot: initializes/unseals OpenBao, then exits |
| `migrator` | built from `src/Dbr.Migrator` | One-shot: applies database migrations, then exits |
| `catalog-sync` | built from `src/Dbr.CatalogSync` | One-shot: applies the curated catalog files, then exits |

`openbao-init`, `migrator` and `catalog-sync` exiting is normal and expected — they are one-shot jobs, not
services. `api` and `worker` wait for all three to finish *successfully* before they start, so a
failed migration stops the stack instead of letting the application run against a schema it
doesn't match — and a catalog that will not apply stops it instead of letting the application
answer from content nobody approved. If you see `api` and `worker` stuck in `Created`, read the
`migrator` and `catalog-sync` logs first:

```bash
docker compose logs migrator
docker compose logs catalog-sync
```

### Why OpenBao and not HashiCorp Vault

HashiCorp Vault relicensed to BUSL-1.1 at v1.15 and is no longer open source. This project is
AGPL and commits to every bundled dependency being open source, so it uses
[OpenBao](https://openbao.org/) — the Linux Foundation fork, MPL-2.0 — instead. The Transit API is
compatible, so if you'd rather run Vault, it's an image swap in `docker-compose.yml`, not a code
change.

## Reaching the services from your machine

By default **only the API publishes a host port** (`8080`). Postgres, RabbitMQ, and OpenBao talk to
each other over the compose network and bind nothing on your host — so the stack starts cleanly
even if you already run Postgres on 5432.

To reach them directly (psql, the RabbitMQ management UI, the OpenBao web UI), add the overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev-ports.yml up
```

| Service | URL | Credentials |
|---|---|---|
| API | http://localhost:8080 | — |
| RabbitMQ management UI | http://localhost:15672 | `dbr` / `dbr_dev_password` |
| OpenBao UI | http://localhost:8200 | Token: `dbr_dev_root_token` |
| Postgres | `localhost:5432` | `dbr` / `dbr_dev_password`, database `dbr` |

If a port collides with something you already run, override it in `.env`
(`POSTGRES_PORT`, `RABBITMQ_MGMT_PORT`, `BAO_PORT`, `API_PORT`).

## Data persistence

All three stores keep their data in named Docker volumes, which **survive `docker compose down`
and container deletion**. Containers are disposable; the volumes are not.

| Command | Effect |
|---|---|
| `docker compose down` | Removes containers. **Data is kept.** |
| `docker compose down -v` | **Deletes all data** — the reset switch |
| `docker volume prune -a` | Also deletes these volumes if the stack is down. Careful. |

The volumes are `dbr_postgres-data`, `dbr_rabbitmq-data`, and `dbr_openbao-data`, stored inside
Docker's VM rather than as browsable files. Resetting Docker Desktop to factory defaults destroys
them.

To start completely fresh:

```bash
docker compose down -v && rm -f .openbao/openbao-init.txt
```

## Database migrations

The schema is plain SQL under [`db/migrations/`](db/migrations/), applied by the `migrator`
service on every `docker compose up`. It is idempotent — already-applied scripts are recorded in
a journal table and skipped — so bringing the stack up repeatedly costs nothing.

Entity Framework Core is the runtime O/RM here and **never owns the schema**. There are no EF
migrations, and the design-time tooling that would generate them is referenced by no project, so
`dotnet ef migrations add` is not a command this repository can run. Row-level security policies,
partial indexes, and extensions all end up as raw SQL escape hatches under EF's migration model
anyway — at which point the generated C# wrapper stops paying for itself, and the thing being
reviewed may as well be the SQL that will actually run.

Two sets, two journals:

| Set | Scripts | Journal | Holds |
|---|---|---|---|
| core | `db/migrations/core/` | `public.schema_versions_core` | Operational data — jobs, statuses, catalog, audit |
| vault | `db/migrations/vault/` | `public.schema_versions_vault` | The envelope-encrypted PII store |

They point at the same database today, with the vault as a schema inside it. Each has its own
connection string (`ConnectionStrings__Core`, `ConnectionStrings__Vault`), which is what keeps
moving the vault to its own database later a configuration change rather than a rewrite.

To add a migration, drop a `YYYYMMDD_HHMM__description.sql` file in the right folder — it is
compiled into the migrator by a wildcard, so there is nothing to register. **Migrations are
forward-only**, and a script that has already run anywhere must never be edited: the journal is
keyed by filename, so an edit silently reaches no database that already applied it. Correct a
mistake with a new script. See [`db/migrations/README.md`](db/migrations/README.md) for the full
set of rules.

Each script runs inside a transaction, and Postgres DDL is transactional, so a script that fails
halfway leaves the schema exactly as it was — the migrator exits non-zero and nothing starts.

## The tenant boundary

Tenant isolation is enforced by Postgres row-level security, not by remembering to
write `.Where(t => t.TenantId == ...)`. A query that forgets the filter returns
nothing; it does not return someone else's rows.

Three pieces make that true:

1. **`app.tenant_id`** — a session setting carrying the current tenant, written by
   `TenantSessionInterceptor` on every connection open, including writing it *blank*
   when there is no tenant. Connections are pooled, so leaving a stale value behind
   would be a cross-tenant read that looks exactly like a correct one.
2. **`app.current_tenant_id()`** — reads that setting. Unset or blank resolves to
   `NULL`, and `tenant_id = NULL` is `NULL` rather than true, so a connection that
   never identified a tenant matches zero rows. The fail-closed behaviour falls out
   of SQL's three-valued logic rather than depending on a check somewhere.
3. **`dbr_app`** — the role the application acts as, via `SET ROLE`.

That third piece is the one that is easy to get wrong, and worth stating plainly: **a
policy alone would isolate nothing here.** Postgres skips row-level security for
superusers, for roles holding `BYPASSRLS`, and — unless the table is `FORCE`d — for
the table's own owner. The role this stack connects as is all three. `dbr_app` is
`NOSUPERUSER NOBYPASSRLS`, so the rules apply to it; it is also `NOLOGIN`, reached by
`SET ROLE` over the existing connection rather than by authenticating, so no second
credential has to be provisioned, distributed or rotated.

A fourth piece sits above the database, as defence in depth rather than as the boundary itself:
an entity implementing `ITenantScoped` gets an EF Core query filter narrowing it to the current
tenant, applied by convention to every such entity rather than listed per type. It is not what
keeps tenants apart — the policies are, and they hold whether it runs or not. It exists because the
policies are enforced somewhere the application cannot see, by configuration it does not control: a
table whose migration forgot to opt in, a revoked `FORCE`, a connection that arrived as the wrong
role. In each of those the database would hand back every tenant's rows, and the application asks
for only its own anyway.

A tenant-scoped table opts in from its own migration:

```sql
CALL app.enable_tenant_rls('public.tenant');
```

which enables and forces RLS, creates the `tenant_isolation` policy over both reads
(`USING`) and writes (`WITH CHECK`), and grants `dbr_app` its DML. It refuses a table
with no `tenant_id` column rather than creating a policy that fails later at query
time. Two optional arguments cover the exceptions: a scoping column, for a table
scoped by something other than `tenant_id`, and a role name, for the vault tables
`dbr_app` is not allowed to touch at all:

```sql
CALL app.enable_tenant_rls('public.tenant', 'id');
CALL app.enable_tenant_rls('vault.profile_identity', 'tenant_id', 'dbr_vault');
```

> **Not a defence against arbitrary SQL.** The application connects with a role that
> could `RESET ROLE`. This boundary is aimed squarely at a missing tenant filter in
> application code, and it makes that failure closed rather than silent. A deployment wanting the stronger property should connect as a dedicated
> login role that is not a superuser; nothing here has to change for that to work.

### What sits outside it, and why that is not a gap

Three kinds of table deliberately never call the procedure above:

| Table | Why |
|---|---|
| `passkey_ceremony` | A ceremony exists precisely while there is no tenant to scope it to — that is what logging in and signing up mean. Its unguessable primary key stands in, and the rows are kept correspondingly cheap and short-lived |
| `broker`, `legal_basis`, `broker_legal_basis` | Shared reference data. A broker is a company and a legal basis is a statute; neither belongs to an account, and both are public facts |
| `broker_health` (later) | Pacing state shared *across* tenants for one broker |

For the catalog the risk runs the other way round from everywhere else. The usual danger
is a table that should be scoped and is not. Here it is a table that is shared and gets
scoped anyway — every tenant would read an empty catalog, every removal would fall back
to an operational deadline, and nothing would look broken except the answers. So the
tests assert the inverse of the isolation tests: two different tenants, **and a
connection carrying no tenant at all**, all read the same rows.

The catalog is also the one place `dbr_app` holds `SELECT` and nothing else. Curated
content arrives by a reviewed path, which means the code that works out a statutory
deadline provably cannot edit the statute it worked from.

## The vault: where identifying data lives

Names, addresses, contact details and dates of birth do not live alongside accounts, jobs and
statuses. They live in a separate store — today the `vault` schema in the same database, later a
database of its own — reached only through the profile service, and encrypted before they get
there.

**Two roles, each blind to the other's tables.** This is the part that makes the separation real
rather than tidy:

| Role | Can reach | Cannot reach |
|---|---|---|
| `dbr_app` | `public` — accounts, sessions, profiles' non-identifying half | the `vault` schema, at all |
| `dbr_vault` | `vault` — encrypted identity rows | `public`, at all |
| `dbr_scheduler` | one column of `tenant` — the list of account ids | every other table, and every write |

Each connection assumes one of them on open, decided by which context opened it. A query issued
over the core connection cannot read an encrypted name; a query issued over the vault connection
cannot bring an email address alongside one. "Never joined into general query paths" is therefore
something Postgres refuses rather than something reviewers have to notice — and it does not
silently start working the day the two stores share a database again.

That split is not a defence against a process that can run arbitrary SQL, for the same reason
`dbr_app` isn't: both roles are reached with `SET ROLE` over one credential. The
credential-level version is available whenever a deployment wants it, and costs one line —
`ConnectionStrings__Vault` is already separate, so pointing it at another database, or at the
same one as a different user, changes nothing else.

**A profile is stored in two halves.** `public.privacy_profile` holds what routing needs: whose
it is, what relationship the tenant claims to that identity, a coarse residency region like
`US-CA`, and which attestation was accepted. `vault.profile_identity` holds the identity itself,
encrypted. The region is deliberately in the first half and constrained to stay coarse —
resolving which statute governs a removal happens on every request, and that must never require
a decryption.

**Envelope encryption, per profile.** Each profile row gets a data key from OpenBao; the fields
are encrypted locally with it, and only the wrapped form of the key is stored. Each of the four
field groups is encrypted separately, so a worker sent to fill in one broker's form can later be
released a name without a date of birth being decrypted to do it. The ciphertext is bound to the
tenant, the profile and the field it belongs to, which means a row copied to another profile — by
a mistaken `UPDATE`, a partial backup restore — fails to decrypt rather than showing one person's
identity under another's account.

Destroying a tenant's wrapping key is what makes account deletion real: every data key it wrapped
becomes permanently unreadable, including copies in a backup nobody can reach to delete, and no
other tenant is affected.

The API is the only service that gets a vault connection string. The Worker gets neither that nor
key-manager credentials — a process that talks to third-party broker sites holding standing
decryption rights is exactly what the design is arranged to avoid.

### The routes over it

All four require a bearer token, and none of them names an account or a profile id: `/profile` is
the account's own, and there is exactly one.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/profile` | The identity, decrypted for its owner |
| `PUT` | `/api/v1/profile` | Replace the names, date of birth, contacts and residency region |
| `POST` | `/api/v1/profile/addresses` | Add an address, current or historical |
| `DELETE` | `/api/v1/profile/addresses/{id}` | Remove one |

**`PUT` does not touch addresses**, and that is deliberate rather than an omission. It replaces
what it is sent — an omitted `names` is an empty one — so a client editing a phone number would
otherwise erase every address it did not resend, and an address somebody lived at years ago is
frequently the only reason a broker listing can be found at all. Addresses are edited one at a
time instead.

Two answers are worth knowing about in advance:

- **`409 Conflict`** means the profile changed while you were editing it. Fields are encrypted as
  a whole and rewritten under a fresh key on every change, so two overlapping edits cannot be
  merged — the second would silently undo the first. Fetch it again and reapply.
- **`404`** on `GET /api/v1/profile` means this account has no profile. Signup creates one, so no
  current account is in that state — only accounts opened before it did.

What a profile may hold is capped — names, contacts and addresses each have a limit, and so do
the field lengths. Ordinary tables get that from the schema; these are encrypted columns the
database cannot read, so the limits live in the API and are the whole of the ceiling.

## What the account permits

Three separate permissions, not one checkbox:

| Scope | What it allows |
|---|---|
| `scan` | Searching brokers for this account's identities |
| `auto_removal` | Opening removal requests for what a scan finds |
| `auto_resubmit` | Opening one again when removed data reappears |

They are separate because they are different asks. A search is something nobody else sees;
opening a removal request puts somebody's name and address in front of a broker in a message
sent as them. Wanting the first without the second is a reasonable position, and one blanket
agreement would make it unexpressible.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/profile/consent` | All three, and the consent text this instance serves |
| `POST` | `/api/v1/profile/consent` | Grant or withdraw one, as `{"scope": …, "granted": …, "policyVersion": …}` |

They sit under `/profile` because that is where somebody looks for them, but they belong to the
**account** rather than to that profile: they cover every identity it manages. Adding a second
identity already takes its own explicit attestation, so asking again per profile for the same
three permissions would be friction bought with nothing.

`GET` always answers with all three, including the ones nobody has decided about — those come
back `granted: false` with a null `since`. A client renders one switch per scope and needs a
position for each; a missing entry would be a position it had to invent. **Never having been
asked is not permission**, and nothing runs on somebody's behalf because they have not got
around to refusing it.

Two things worth knowing before you build against it:

- **Withdrawing does not erase the grant.** Every decision is a new row; the newest one for a
  scope is what is in force. That is the whole reason the record exists — the question that gets
  asked months later is not whether a scan may run now, but whether it was permitted when it
  ran, and under what wording. The application role holds no `UPDATE` on that table, so nothing
  can quietly turn the history into a switch.
- **`409 Conflict`** means the consent text moved since the client displayed it. Fetch the
  current `policyVersion`, show that text, and ask again. What gets recorded is what somebody was
  actually shown, not what a client claimed — same stance the terms take at signup, and a
  separate document on a separate clock.

## The broker catalog, readable by anybody

Four routes, and **the only ones in this API that need no token**:

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/brokers` | The active catalog, filterable by `removalMethod` and `legalBasisId` |
| `GET` | `/api/v1/brokers/{id}` | One broker, with the regimes confirmed to govern it |
| `GET` | `/api/v1/legal-basis` | Known regimes, filterable by `residencyScope` and `requestType` |
| `GET` | `/api/v1/legal-basis/{id}` | One regime, with its citation and reviewer |

Nothing here belongs to an account — a broker is a company and a legal basis is a statute — so
there is nobody to scope the answer to. Requiring a token would mean opening an account before
you could find out whether this instance could help you at all, and the answer would be the same
one either way.

Filter values are the same spellings the database holds: `webform` / `email` / `api` / `postal`,
and `delete` / `opt_out_sale` / `opt_out_targeted_ads`. A value that is not one of them is a
`400` naming the parameter rather than a list with the filter quietly dropped — an ignored filter
answers a different question than the one asked and looks like a complete list. An **empty**
parameter (`?removalMethod=`) is a filter nobody set, which is what a form control with nothing
selected sends. A `residencyScope` is a coarse region code (`US-CA`, `EU`) and is upper-cased for
you.

Two things the responses are careful about:

- **`operationalSlaDays` on a broker is a courtesy target, not a deadline.** A removal governed
  by a statute takes its deadline from the regime instead, and `legalBases` on the detail route
  is what says whether one applies. An empty `legalBases` means nobody has confirmed a statute
  reaches that company — applicability turns on revenue and data-volume thresholds this system
  cannot check, so it is confirmed by a person or not at all. It is not a claim that no statute
  does.
- **Pacing is not published.** How many jobs a broker's lane runs at once, the delay between
  them, and how many rate-limited answers open the circuit breaker are all in the catalog row and
  none of them are on the wire. They are this instance's tuning for talking to a company rather
  than facts about it, and the exact number of refusals that stops it trying is only useful to
  somebody who wants it to stop.

The listing is active entries only. A broker the operator has deactivated still answers on its
own route, with `active: false` — somebody holding a link to it should be told it is not being
worked, not told the company was never here.

Writes are deliberately absent. The role serving these requests holds `SELECT` on the catalog and
nothing else, so the code that computes a statutory deadline cannot edit the statute it computed
from; curated content arrives by a reviewed path with the privileges migrations run with.

### What ships in the legal-basis catalog

A migration seeds fifteen rows covering five US states — California (`CCPA`), Virginia (`VCDPA`),
Colorado (`CPA`), Connecticut (`CTDPA`) and Utah (`UCPA`) — each with deletion, opt-out of sale, and
opt-out of targeted advertising. These five come first because that is where the broker volume is,
not because they are the only states with a statute.

> **These rows are maintainer-reviewed, not counsel-reviewed.** Each carries
> `reviewed_by = '@mveregge23'` and the date it was read, which is what the API returns and what any
> client displaying provenance will show. Every row was checked against the primary source it cites
> — statute text, or a state Attorney General's published guidance. That is real provenance and it
> is what this project asks for at this stage. **It is not a legal opinion.** If you are running an
> instance that quotes these deadlines to people, get them in front of somebody qualified.

An incomplete catalog is safe; a wrong one is not. A jurisdiction with no row falls back to the
broker's own `operationalSlaDays`, which is presented as a courtesy target — so removing rows you
do not trust degrades the service honestly rather than breaking it:

```sql
DELETE FROM legal_basis WHERE code = 'UCPA';
```

### Editing the catalog

Legal-basis content lives in [`catalog/legal-basis/`](catalog/legal-basis/) as one YAML file per
jurisdiction, and that is the only place to change it. A statutory correction is a diff in a file,
on a pull request, reviewed at the two-approval bar `.github/CODEOWNERS` sets for that path — which
is a review counsel can actually do, unlike a migration.

The `catalog-sync` service applies those files on every `docker compose up`, after the migrator and
before the API or worker start. It connects as the owning role, because the application is granted
`SELECT` on the catalog and nothing more.

```
catalog/legal-basis/us-ca-ccpa.yaml   ->  catalog-sync  ->  legal_basis rows
```

Three things worth knowing before you edit one:

- **The sync only touches rows it owns.** Every row carries `source`, either `catalog` or `local`.
  The sync inserts, updates and removes `catalog` rows to match the files, and never touches a
  `local` one — so a reading of your own survives both an update and a retraction of the shared
  content. It reports what it left alone rather than silently skipping it.
- **Deleting a file retracts the row.** That is the point of the split: a regime read wrongly and
  corrected stops governing requests on the next deploy, instead of lingering until somebody runs a
  `DELETE` on every install. If brokers are still confirmed against the regime, the retraction is
  refused with an explanation — those confirmations are a reviewed judgement and the schema will not
  drop them as a side effect.
- **Your own rows are yours.** Anything inserted by hand defaults to `source = 'local'`. To take a
  shipped row over permanently, set its source to `local` and the sync leaves it alone from then on.

`dotnet run --project src/Dbr.CatalogSync -- --check` reads and validates the files without touching
a database; CI runs exactly that on every pull request, so a malformed file fails review rather than
a deploy.

The original seed migration is still in `db/migrations/` and still runs on a fresh database — it is
applied history, not a second source of truth. The five jurisdictions it inserted were handed to the
catalog when `source` was added, and the files have owned them since.

Deadlines carry their own unit. `response_deadline_days` is a count and `deadline_unit` says how to
count it — `calendar` for fourteen of the fifteen rows, `business` for California's opt-out clock,
which the statute expresses as "no later than 15 business days". Both reach the API, and a client
showing the number without the unit will misstate the deadline by most of a week.

The alternative was to convert at seed time and store 21 calendar days. Storing the rule as written
keeps the row checkable against the citation printed beside it, and leaves the conversion to the
code that computes an actual date — the only place that knows when the clock started and can skip
weekends and public holidays properly. `deadline_unit` governs `extension_days` too.

## Asking for a scan

A scan is one run of "ask these brokers what they hold about this identity". You ask for one with:

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/v1/scans` | Ask for a scan, as `{"profileId": …, "brokerIds": […]}` — both optional |
| `GET` | `/api/v1/scans` | Every scan this account has asked for, newest first |
| `GET` | `/api/v1/scans/{id}` | One scan, with the brokers it was narrowed to |

All three need a token, unlike the catalog's routes. Everything here belongs to an account, and
there is no version of "what was found about me" that can be answered without knowing whose it is.

**There is nowhere in that request to put a name.** `profileId` names one of the identities this
account has already created and attested to; omit it and it means your own. That is the whole
shape of the thing: a scan is structurally a lookup, and an endpoint that accepted a free-text
identity would be a people-search engine with a removal tool attached. The guarantee is the
request having no such field rather than a rule that checks one — and the database says it a
second time, since a scan's foreign key is over the tenant and the profile together, so a row
pointing at somebody else's identity cannot exist rather than merely being unreadable.

Sending one anyway is **refused, not ignored**. `POST /scans {"name": "Jane Doe"}` returns
`400`, because a field this API quietly dropped would hand back a perfectly good scan of your
own profile and let whoever wrote the client conclude that name-based search works. That applies
to every route here, not just this one: a request body carrying a field the API does not
implement is an error.

`brokerIds` narrows the run. Leave it out for the whole catalog; an id that is not in this
instance's catalog is refused, with every bad id named, rather than quietly dropped — a scan over
fewer brokers than you asked for, reported as the scan you asked for, is a smaller answer that
looks complete.

Two responses worth knowing about before you build against it:

- **`403 Forbidden`** means this account has not granted the `scan` consent scope, or has
  withdrawn it. It is not an authentication problem and a fresh token will not help; grant the
  scope (see [What the account permits](#what-the-account-permits)) and ask again. The check runs
  on every request rather than once at signup, so withdrawing permission stops the next scan.
- **`202 Accepted`**, not `201`. The run has been taken on and has not happened.

And the part to be aware of if you are running this today: **a queued scan stays queued.** The
per-broker worker lanes that would pick one up are a later story, so `POST /scans` records the
request and nothing executes it yet. `KNOWN-GAPS.md` has the detail.

## What the scans found

An exposure is one broker appearing to hold data about one of your identities.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/exposures` | Findings, newest first, filterable by `status` and `brokerId` |
| `GET` | `/api/v1/exposures/{id}` | One finding |
| `POST` | `/api/v1/exposures/{id}/dismiss` | Say a finding is not you |

Each finding arrives with the broker it is on — id, name, domain and removal method — so a
client never has to resolve ids against the catalog to render a list. The pacing fields stay
off the wire here exactly as they do on the catalog routes: how this instance decides to talk
to a company is not part of what you were found on.

`status` is one of `new`, `requested`, `removed`, `reappeared`, `dismissed`. **An unrecognised
value is refused, not ignored** — the alternative is a `200` with an empty array, which reads as
"you are not listed anywhere", and that is a sentence somebody would act on.

**Dismissing is a judgement, not a delete.** It records that a match is somebody else, which is
the one call in this API only the person themselves can make — nothing is ever sent in your name
over a listing you have said is not you. The row stays: it is what stops a later scan re-offering
the same listing as a fresh discovery. Dismissing twice answers the same as dismissing once.

One refusal to know about: **`409 Conflict`** means a removal request is already open against
that listing. Saying it is not you while something is in flight in your name over it would leave
the contradiction standing at the broker rather than resolving it, so cancel the request first.
(No removal request can exist yet — that is a later story — but the rule is in place so it does
not have to be retrofitted around one.)

Findings are not paginated. A listing that comes back after removal reappears on the row that
already knows its history rather than as a new row, so this is bounded by the size of the
catalog rather than growing with time.

## Monthly scans, and the day yours falls on

The worker plans recurring scans. Every account is scanned monthly — provided it has granted the
`scan` consent scope — and the day of the month is **derived from the account id**, not from when
you signed up and not from a column anybody can edit.

That spread is the point. If every account were scanned on the 1st, every broker in the catalog
would see the entire service arrive at once and spend the rest of the day being throttled. The id
is hashed into one of 28 days; 28 rather than 31 so that nobody's monthly scan lands on a date
that does not exist in February.

The job itself wakes daily and asks which accounts are due today:

| Setting | Default | What it does |
|---|---|---|
| `ScanSchedule__Enabled` | `true` | Whether the planner runs at all |
| `ScanSchedule__DailyAtHourUtc` | `2` | The UTC hour it wakes up |
| `Consent__PolicyVersion` | `2026-06-01` | Must match the API's — the worker reads consent too |

Which day *your* account falls on is not configurable, deliberately: it is what keeps the load
spread, and an operator able to set it per account would be able to undo that one account at a
time without noticing.

Two things worth knowing if you run this:

- **Consent is checked on every run**, not once. Withdrawing the `scan` scope stops next month's
  scan; nothing has to remember to cancel a schedule.
- **Running the planner twice in a day queues nothing the second time.** A restarted worker, a
  replayed misfire, or a second worker somebody started by mistake are all harmless — there is a
  unique index behind it, so this holds even when two of them check at the same instant. Scans you
  ask for by hand are not affected; asking twice in a day is your prerogative.

The planner needs to know which accounts exist, which no tenant-scoped role can answer. It uses
`dbr_scheduler` for exactly that one query — one column of one table, no writes — and then does
everything with a consequence as `dbr_app`, one account at a time, inside the same boundary as
every other write in this system.

## Signing in: passkeys

Accounts are opened and entered with a passkey. There is no password, and no step where you
type an address and are told whether it has an account here.

That second part is the design constraint everything else follows from. This service exists for
people trying to reduce what is known about them, so an endpoint answering *"is
someone@example.com registered?"* would be handing out exactly the kind of fact the service is
meant to remove. So sign-in asks for no identifier at all:

1. `POST /api/v1/auth/login/options` — no request body. The server issues a challenge naming no
   account and no credential.
2. The browser offers whatever passkey it holds for this site and signs the challenge.
3. `POST /api/v1/auth/login` — the server works out whose account it is *from the credential
   that answered*.

Signing up is the same shape, with an address so the passkey has a label in your authenticator:
`POST /api/v1/auth/register/options` with `{"email": "..."}`, then `POST /api/v1/auth/register`
with what the browser produced. **Nothing is written until the authenticator answers** — an
abandoned signup leaves no account behind.

The registration challenge also comes back with a `termsVersion`, and finishing the signup means
sending it back as `acceptedTermsVersion` alongside the credential. Display that version's text;
what the client echoes is compared against what this instance is currently serving and refused
with `409` if it has moved on, so what gets recorded is what somebody was shown rather than what
a client claimed. A refusal does not spend the registration — accept the current version and
finish the same ceremony.

**Signing up creates the account's own profile.** There is no separate step, and `GET
/api/v1/profile` works on a brand-new account — it comes back empty, waiting to be filled in.
This is the common case the whole design is pointed at: somebody removing their own data attests
to that by accepting the terms, which is exactly the claim being made. Managing an identity that
is not your own is the deliberately higher-friction path, and is not built yet.

If the key manager cannot be reached, the signup fails and **takes the half-made account with
it** — the address stays free and you can try again. The alternative would be an account that
exists, cannot be signed up for a second time, and can never have the profile every feature
reads from.

Two consequences worth knowing before you deploy this:

- **Passkeys must be discoverable** (resident keys), and the authenticator must verify its
  holder — a biometric or a PIN. An authenticator with no room to store a resident credential
  cannot be used here. That is the cost of never asking who you are, and it is deliberate.
- **Register a second passkey.** An account with one passkey has one way in, and whatever holds
  it can be lost, broken or wiped — see below.

### Keeping more than one way in

An account opened with a single passkey can be reached by exactly one device. If that device is
lost, so is the account: there is no password to fall back on and no recovery flow. **Register a
second passkey on a different device**, ideally one whose passkeys sync to a password manager.

Adding one is the same two-step ceremony as signing up, on routes that require a token — the
account is never named in the request, it comes from the token:

1. `POST /api/v1/account/passkeys/options` — no body. The response names the passkeys the account
   already has, so an authenticator that recognises one of them declines rather than creating a
   duplicate.
2. `POST /api/v1/account/passkeys` — with what the browser produced.

`GET /api/v1/account/passkeys` lists what the account can be reached with, including whether each
one is backed up. That flag is the one worth reading: it separates a passkey synced to a password
manager from one that exists on a single device and goes with it.

**Removing a passkey is not built yet.** A lost device's passkey stays valid until it is, which
matters if the device was stolen rather than dropped in a river — and the endpoint needs a
deliberate answer to what happens when you remove the last one, since that is an account nobody
can reach again.

### Configuring the relying party

A passkey is bound to one domain and is offered on no other. That is what makes it unphishable,
and it means these settings describe **where a browser reaches this instance**, not where the API
listens:

| Setting | Environment variable | Default |
|---|---|---|
| `Passkeys:RelyingPartyId` | `PASSKEY_RELYING_PARTY_ID` | `localhost` |
| `Passkeys:Origins:0` | `PASSKEY_ORIGIN` | `http://localhost:8080` |

The defaults match the compose port mapping, so `docker compose up` works untouched. Beyond that:

- **Change `API_PORT` and you must change `PASSKEY_ORIGIN` to match.** Otherwise the browser
  refuses every ceremony before the server ever sees the request.
- **Set the relying party to your registrable domain, not the exact host.** `example.com` gets
  passkeys that work on `app.example.com` too; `app.example.com` gets passkeys offered on that
  one host and nowhere else.
- **Changing the relying party after accounts exist invalidates every passkey already
  registered.** The browser will no longer offer them, and there is no migration for it. Pick it
  before anyone signs up.

Startup refuses a configuration that cannot work — a relying party written as a URL, an origin
outside it, an empty origin list. That is deliberate: every one of those mistakes otherwise
surfaces as a browser silently refusing a ceremony, which needs a real authenticator to reproduce
and says nothing about which setting was wrong.

## Sessions and tokens

Signing up or signing in returns two tokens, and they do different jobs:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "accessTokenExpiresAt": "2026-08-13T09:15:00Z",
  "refreshToken": "0Yg7m2...",
  "refreshTokenExpiresAt": "2026-09-12T09:00:00Z"
}
```

- The **access token** is a JWT, sent as `Authorization: Bearer <token>` on every request. It is
  checked by verifying its signature and asking the database nothing, which is what makes ordinary
  requests cheap. It carries an account id and the timestamps that expire it — nothing else, since
  anyone holding it can read it.
- The **refresh token** is an opaque secret, exchanged at `POST /api/v1/auth/refresh` for a new
  pair. It is good exactly once.

### Rotation, and what happens when a token is stolen

Every refresh spends the token presented and issues a new one. Keep what you get back — the old
one stops working immediately.

That is not bookkeeping. It is what turns a stolen refresh token from a permanent key into a race:
whoever uses it first invalidates it for the other, and the loser's next attempt presents a token
that has already been spent. **A spent token being presented again ends the whole session** — every
token descended from that sign-in, including the one currently in someone's hands. Both parties
have to sign in again, which is the only safe answer when there is no way to tell which of them is
the legitimate one.

Sessions also have a deadline rotation cannot move. A refresh token is good for 30 days and each
rotation grants a fresh 30, but the session itself dies 90 days after the sign-in that started it,
however often it has been refreshed.

| Setting | Environment variable | Default |
|---|---|---|
| `Tokens:SigningKey` | `TOKEN_SIGNING_KEY` | none — startup fails without it |
| `Tokens:AccessTokenLifetime` | `Tokens__AccessTokenLifetime` | `00:15:00` |
| `Tokens:RefreshTokenLifetime` | `Tokens__RefreshTokenLifetime` | `30.00:00:00` |
| `Tokens:SessionLifetime` | `Tokens__SessionLifetime` | `90.00:00:00` |

**The signing key has no default and the API will not start without one.** A key committed to a
public repository would mint valid access tokens for every deployment that never replaced it, so
there is no value that could safely ship here. The compose file supplies a development one the same
way it supplies a database password; `openssl rand -base64 48` produces a suitable replacement. It
must be at least 32 bytes — a shorter key does not fail, it just makes the signature easier to
forge than it looks.

### Signing out, and the gap it leaves

`POST /api/v1/auth/logout` ends the session the refresh token belongs to. It always answers `204`,
whether or not the token meant anything, so it cannot be used to ask whether a token found
somewhere is still worth something.

**An access token already issued keeps working until it expires.** Nothing consults the database on
an ordinary request — that is exactly what makes ordinary requests cheap — so signing out revokes
the session but not the bearer token in flight. The window is `Tokens:AccessTokenLifetime`, fifteen
minutes by default. Shorten it if that trade is wrong for you; the cost is a refresh round trip
more often.

### Suspending an account

`tenant.status` is a base gate that every deployment enforces, independently of billing. A
self-hosted operator suspending an abusive user of their own instance — a shared household
deployment, say — uses the same mechanism a hosted instance would:

```sql
UPDATE tenant SET status = 'suspended' WHERE id = '...';
```

A suspended account cannot sign in, and cannot renew a session it already had. The second half is
the one that matters: a session outlives the sign-in that created it, so a gate only at sign-in
would let a suspended account keep renewing access indefinitely on a token it obtained the day
before.

**Suspension is not deletion.** The session is left intact rather than revoked, so setting the
status back to `active` restores what was there. And the same fifteen-minute caveat applies: an
access token already issued keeps working until it expires.

There is no endpoint for this yet — it is a database change, made by whoever operates the
instance.

## OpenBao, sealing, and the unseal key

OpenBao encrypts its storage with a master key, which is itself protected by an **unseal key**. On
startup the barrel is *sealed*: the data is there, but unreadable until the unseal key is supplied.

So that `docker compose up` doesn't require you to paste a key every time, the `openbao-init`
service writes the unseal key and root token to `.openbao/openbao-init.txt` (mode `600`) and
re-reads them to unseal automatically on subsequent starts.

**This file is gitignored and unique to your machine.** It is generated locally on first run;
nothing about it is shared, and no key material is ever committed to this repository.

Two things to know:

- **The keyfile and the data volume are a matched pair.** Delete the keyfile but keep the volume
  and the data is permanently unreadable — `openbao-init` will detect this and tell you to
  `docker compose down -v`. Delete the volume and the keyfile is simply regenerated.
- **This arrangement is a local-development convenience, not a deployment pattern.** Storing the
  unseal key beside the data it protects gives up sealing as a security control while keeping it
  as a durability mechanism. That trade is fine on your laptop and wrong on a server — see below.

### What the application is allowed to ask the key manager for

The API authenticates to OpenBao with a token scoped to
[`openbao/policies/dbr-api.hcl`](openbao/policies/dbr-api.hcl), applied by `openbao-init` on every
`docker compose up`. Editing that file and bringing the stack up is enough to change it.

It grants three things — create/configure/delete a wrapping key named `tenant-*`, mint a data key
under one, and decrypt one — and, because policies are deny-by-default, withholds everything else.
Two of those absences are the point:

- **It cannot list keys.** Keys are named after tenants, so listing them would enumerate every
  account on the instance — the question the whole sign-in design refuses to answer. Being able to
  decrypt and being able to find out who exists are different powers, and this token has only the
  first.
- **It cannot encrypt chosen data under a tenant's key.** Nothing needs to: the only thing a
  wrapping key ever wraps is a data key, and OpenBao mints those itself.

**The Worker holds no key-manager credentials at all.** It drives browsers against third-party
sites, so a token that can decrypt would be a standing decryption right sitting in the most exposed
part of the system. When a job needs a tenant's fields it will ask for a short-lived release of
only those fields from the service that does hold the keys — which can refuse, and can record that
it was asked. That release path is not built yet, which is why the Worker currently has nothing to
decrypt with and nothing to decrypt.

## Logs, and what cannot get into them

Both services log through Serilog. A developer's terminal gets a readable line; everywhere else
gets one JSON object per line, which is what `docker compose logs` collects and what the OTLP
sink will ship later.

Levels are yours to set, under `Serilog` rather than the `Logging:LogLevel` section .NET
projects usually carry — Serilog owns the pipeline here, and the default providers are removed
at startup so nothing is left registered that could write a line around it:

```jsonc
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": { "Microsoft.AspNetCore": "Warning" }
  }
}
```

**A log line carries ids and enums, never the fields behind them.** "Removal `{id}` for broker
`{id}` moved to `Failed`" is the shape; the name or address that removal was about is not in it.
That is a rule about how calls get written, and rules enforced only by attention eventually are
not — so there are four layers under it, each covering what the one above cannot:

| Layer | Catches |
|---|---|
| The identity types' own `ToString` | Anything that turns one into text — interpolation, concatenation, an exception message |
| A Serilog destructuring policy | `{@Profile}`, which would otherwise unpack every member |
| The redaction enricher | Properties named after an identity field, at any depth, and values that are address-shaped whatever they are called |
| A formatter wrapper | The rendered line, including the one thing nothing else can reach — an exception's message |

The build closes the remaining gap. A message built by string interpolation arrives as a
finished string with no properties in it, so there is nothing left for the enricher to
recognise; `CA2254` is turned on as an **error** so that call never compiles. Write
`logger.LogInformation("... {ProfileId}", id)`, not `logger.LogInformation($"... {id}")`.

### What is deliberately still readable

The redaction is narrow on purpose, because a log you cannot debug from is its own kind of
failure. **Everything below survives untouched:**

- Every id — `TenantId`, `ProfileId`, `BrokerId`, `RemovalRequestId`, `JobId`, `ExposureId`, and
  the ids of individual addresses and contacts
- Every enum, status and state transition; attempt numbers, counts, timestamps, durations
- Every property on a framework logger — request paths, status codes, EF command text and its
  parameters, the listening address, the environment name
- **Broker and catalog vocabulary**: `Name`, `City`, `Street`, `Contact`, `Identity` and `Fields`
  are deliberately *not* on the deny list. A broker has a name, a city and a contact mailbox and
  none of them belong to a tenant, so redacting them would cost exactly the line somebody
  debugging a broker is reading

What is withheld is a tenant's own identity: `Names`, `FullName`, `GivenName`, `Surname`,
`DateOfBirth`, `Email`, `Phone`, `Contacts`, `Address`, `Addresses`, `Line1`, `Line2`,
`PostalCode` — plus anything of the identity types, matched by type whatever the property was
called. Allowing the vague words back cost nothing: a tenant's name reaches a log through
`ProfileIdentityFields` or `ProfileDetails`, and those are caught by type.

Three things worth knowing if you are adding logging:

- **The name list is scoped to this codebase's own loggers.** `Address` here means where somebody
  lives; in ASP.NET Core's own events it means a listening URL, and an earlier version of this
  redacted both — the API came up announcing it was listening on `[redacted]`. Framework events
  keep their own vocabulary, and are still subject to the value and type rules.
- **Email-shaped values are withheld everywhere, including a broker's opt-out mailbox.** That one
  is a deliberate cost rather than an oversight. The rule cannot be scoped to our own loggers,
  because EF Core writes a failed command at error level with the exception attached — so a second
  signup at an address already registered puts that address into an event sourced from
  `Microsoft.EntityFrameworkCore.Database.Command`. Log which broker a removal went to, not which
  mailbox; the mailbox is catalog data and is one lookup away.
- **`[redacted]` in a line is the mechanism working, not a bug** — but `[redacted]` where you
  expected a broker field usually means a property was named after something on the list. Rename
  the property rather than working around the redactor.

## Working on the code without Docker

```bash
dotnet build Dbr.slnx      # build everything
dotnet test  Dbr.slnx      # run the test suite
```

The solution is `Dbr.slnx` (the XML solution format that .NET 10 emits by default). Projects live
under `src/` and tests under `tests/`, with shared MSBuild properties in `Directory.Build.props`:

```
Dbr.Domain  ←  Dbr.Infrastructure  ←  Dbr.Api
                                   ←  Dbr.Worker
```

Tests are xUnit v3 and run on Microsoft.Testing.Platform, so each test project is also an
executable — `dotnet run --project tests/Dbr.Infrastructure.Tests` runs just that project's tests
and prints per-test output, which is usually what you want while iterating.

| Project | Needs Docker | Covers |
|---|---|---|
| `Dbr.Infrastructure.Tests` | no | Mapping conventions, the tenant context, what the interceptor sends |
| `Dbr.Migrator.Tests` | no | Migration filenames, set membership and ordering, misconfigured runs |
| `Dbr.Integration.Tests` | **yes** | Tenant isolation and the migrations, against a real Postgres and a real OpenBao |

`Dbr.Integration.Tests` starts its own throwaway containers with Testcontainers — it does not use
the compose stack, and does not care whether that stack is running. It needs a working Docker
daemon; without one, those tests fail rather than skip, because a green run that quietly skipped
the only tests capable of observing tenant isolation would be worse than a red one.

Tenant isolation is the one thing in this repository that cannot be tested any other way. The
property under test is what Postgres itself does when a session variable is missing, so an
in-memory provider — which has no policies, no roles and no `current_setting` — would report
success whether the boundary existed or not.

To run the API against the containerized infrastructure, start the backing services only:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev-ports.yml up postgres rabbitmq openbao-init -d
dotnet run --project src/Dbr.Api
```

The overlay is what publishes Postgres on `localhost:5432` and OpenBao on `localhost:8200`; the
`Development` settings files point the API and Worker at both, and carry a development token
signing key. Outside compose, and outside `Development`, both services need
`ConnectionStrings__Core`, `Tokens__SigningKey` and `Bao__Address`/`Bao__Token` set, and the API
additionally needs `ConnectionStrings__Vault`, `Terms__CurrentVersion` and
`Consent__PolicyVersion` — they refuse to start
without any of them, rather than failing later on the first request that needs a database, a
token, a key, an account to open, or a permission to record. The Worker has
no vault connection string and no key-manager credentials on purpose; see
[the vault](#the-vault-where-identifying-data-lives).

Every source file carries an SPDX header:

```csharp
// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later
```

## Deployment modes

Same codebase, two composition roots:

| | Self-hosted | Maintainer's hosted instance |
|---|---|---|
| Queue | RabbitMQ (bundled) | RabbitMQ, operated by maintainer |
| Key management | OpenBao (bundled) | OpenBao, or a cloud KMS adapter |
| Billing | Not referenced at all | Enabled, behind `IBillingProvider` |
| Access check | `NoOpAccessPolicy` — always allows | `SubscriptionAccessPolicy` |
| Tenancy | Usually one operator, still multi-tenant capable | Many independent tenants, isolated by RLS |

Billing is a separate assembly a self-hosted build never references, so self-hosting needs nothing
from a payment provider to work.

## Security: local defaults vs. a real deployment

**The compose file in this repo is a development and self-hosting artifact. It is not a
production deployment.** Its defaults optimize for a working `docker compose up` on a fresh clone,
and several of them are deliberately insecure in ways that are fine on a laptop — where the only
thing on the network is you — and unacceptable on a host anyone else can reach.

If you are only running this locally, you can stop reading here. If you are deploying it
**anywhere with a public IP**, every row below has to change:

| Default | Why it's fine locally | What a network-reachable deployment needs |
|---|---|---|
| Credentials `dbr` / `dbr_dev_password`, committed to the repo | Nothing but the API is reachable from your host, and only you are on it | Real secrets from a secrets manager or environment, never from a committed default |
| OpenBao unseal key on disk beside the data | Convenience; your disk is already the trust boundary | Auto-unseal via a cloud KMS/HSM, or Shamir shares held by different people. **Never** the keyfile pattern |
| App authenticates to OpenBao with a token scoped to `openbao/policies/dbr-api.hcl` | Same policy a deployment uses; only the *token id* is well-known | Nothing to change here — but issue the token through an auth method rather than a fixed id, so it rotates |
| `tls_disable = true` on the OpenBao listener | Traffic never leaves the compose network | TLS terminated at the listener, or a mutually-authenticated mesh |
| `docker-compose.dev-ports.yml` publishing Postgres/RabbitMQ/OpenBao | You need psql and the UIs | Never used. Backing services stay off any public interface |
| Migrations run automatically at startup | One machine, one instance | An explicit pre-deploy step — multiple replicas racing to migrate is exactly what that avoids |
| Passkey relying party is `localhost` over plain HTTP | It is the only origin your browser will reach | Your real domain over HTTPS. Browsers refuse WebAuthn on any non-`localhost` origin without it, so this is enforced whether you set it or not |
| Token signing key `dbr_dev_token_signing_key_...`, committed to the repo | Nobody else can reach the API to use it | A real secret. Whoever knows this key can mint an access token for any account, so it is the single most important value in this table |
| Terms version `2026-06-01`, naming no document that exists | Nobody is agreeing to anything on your laptop | The version of terms you actually serve. Every account records this as what its owner accepted, and a version naming nothing makes that record worthless — the one row here that is not a secret and still has to change |
| Consent policy version `2026-06-01`, naming no document either | Same reason | The version of the consent text you actually serve. Every grant and withdrawal records it, and it is what answers "what was this person shown" long after the wording moved on. A separate document from the terms, so it gets a separate version |

One gap is worth naming rather than leaving to be discovered: **completing a signup for an
address that already has an account answers `409`**, which tells the caller that address is
registered. Reaching that answer costs a full, valid ceremony — an authenticator has to sign this
server's challenge before the address is ever compared — so it is not a probe you can run in a
loop, but it is not nothing either. It closes when signup starts verifying addresses by mail,
which needs a notification path this instance does not have yet; at that point signup answers the
same way whether or not the address is known.

The intended hosted architecture treats the boundary as structural rather than procedural: the
hosted composition root is a different entrypoint from the self-hosted one, so hosted-only
concerns can't be accidentally wired into a self-hosted build, and vice versa.

**A self-hoster running this on a home network is a supported, first-class case** — the threat
model there is genuinely different from a multi-tenant instance holding many people's identity
data, and this project doesn't pretend otherwise. What it does insist on is that the difference be
explicit rather than assumed.

## Troubleshooting

**`ports are not available: ... address already in use`** — something already holds that port. The
base `docker compose up` only needs 8080; if you're using the dev-ports overlay, override the
conflicting port in `.env`.

**`openbao-init` exits non-zero saying the keyfile is missing** — the data volume holds an
initialized barrel whose unseal key is gone, so the data can't be read. Run
`docker compose down -v` to discard it and start clean.

**`api` and `worker` never start, and sit in `Created`** — a one-shot job they depend on failed.
`docker compose logs migrator` and `docker compose logs openbao-init` will say which. A failed
migration leaves the schema untouched, so fixing the script and running `docker compose up`
again is safe.

**`Cannot load library libgssapi_krb5.so.2` in a service log** — harmless. Npgsql looks for the
Kerberos library to see whether GSSAPI authentication is available; Microsoft's .NET runtime
images don't ship it, and this stack authenticates with a password, so the probe fails once per
process and everything proceeds normally. The library is deliberately not installed — it would
add patch surface for a feature nothing here uses.

**RabbitMQ or Postgres seems to have lost its data** — check you didn't run `docker compose down -v`
or `docker volume prune -a`. Note that a volume being present is not proof state is being kept;
verify with a restart test.

**`OpenBao has dropped support for mlock`** — OpenBao 2.x removed mlock and refuses to start if
`disable_mlock` appears in its config at all. Don't add it back.

## Contributing

Contributions are welcome. A few conventions worth knowing up front:

- **Commits need a `Signed-off-by:` trailer** (DCO, `git commit -s`). There's no CLA.
- **One branch per unit of work, merged with `--no-ff`.** Every piece of work gets its own branch
  off `main` and comes back as a merge commit, even when it's a single commit's worth of change.
  The merge commit is what makes the history navigable — `git log --first-parent main` reads as a
  list of completed work rather than a stream of intermediate steps, and any one of them can be
  undone on its own with `git revert -m 1 <merge>` without unpicking what landed after it. A
  fast-forward merge loses both of those properties.
- **Comments explain the reason, not where the reason is written down.** "We take this lock because
  two workers can otherwise claim the same job" is useful to someone reading the code; "per the
  design doc" is not, because it sends them somewhere they may not have and tells them nothing if
  they don't go. Where a decision has a longer story behind it, the commit message is the place for
  that — it stays attached to the change without cluttering the file.
- **If your change affects how another developer runs this locally, update this README in the same
  PR.** New service, changed port, new required tool, new setup step, changed reset procedure — if
  someone with a fresh clone would hit it, it belongs here. A README that drifts from the compose
  file is worse than no README, because it fails at exactly the moment someone is trying to start.

Reviewer expectations vary by path (see `.github/CODEOWNERS`): broker recipes are reviewed as data,
while connectors, legal-basis content, and database migrations carry a higher bar.

Looking for something to pick up? [`KNOWN-GAPS.md`](KNOWN-GAPS.md) lists obligations the catalog
already records that the code does not meet yet — each with what the law requires, what exists
today, and roughly what closing it involves.

### What CI checks

Every pull request runs [`.github/workflows/ci.yml`](.github/workflows/ci.yml). To see the same
result before pushing:

```bash
dotnet build Dbr.slnx -warnaserror        # nothing that warns reaches main
dotnet test  Dbr.slnx                     # needs Docker for the integration tier
dotnet format whitespace Dbr.slnx --verify-no-changes
dotnet format style      Dbr.slnx --verify-no-changes
./scripts/check-spdx-headers.sh
```

`-warnaserror` is passed on the command line rather than set in the project files on purpose:
locally a warning should be something you can see and keep working past, but nothing that warns
belongs on `main`.

The formatting step checks formatting only — analyzer diagnostics are already gated by the build
above, and running them in both places would report the same problem twice and blur what a lint
failure means. `.editorconfig` is deliberately small: every rule in it is one a contributor can
have a PR rejected over, which is worth spending only on decisions that would otherwise be argued
about in review.

## License

[GNU AGPL-3.0-or-later](LICENSE). The network-use clause is deliberate: running a modified version
as a public service obliges you to publish those modifications.
