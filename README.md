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
- [Signing in: passkeys](#signing-in-passkeys)
- [Sessions and tokens](#sessions-and-tokens)
- [OpenBao, sealing, and the unseal key](#openbao-sealing-and-the-unseal-key)
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

`openbao-init` and `migrator` exiting is normal and expected — they are one-shot jobs, not
services. `api` and `worker` wait for both to finish *successfully* before they start, so a
failed migration stops the stack instead of letting the application run against a schema it
doesn't match. If you see `api` and `worker` stuck in `Created`, read the `migrator` log first:

```bash
docker compose logs migrator
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
time.

> **Not a defence against arbitrary SQL.** The application connects with a role that
> could `RESET ROLE`. This boundary is aimed squarely at a missing tenant filter in
> application code, and it makes that failure closed rather than silent. A deployment wanting the stronger property should connect as a dedicated
> login role that is not a superuser; nothing here has to change for that to work.

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

Two consequences worth knowing before you deploy this:

- **Passkeys must be discoverable** (resident keys), and the authenticator must verify its
  holder — a biometric or a PIN. An authenticator with no room to store a resident credential
  cannot be used here. That is the cost of never asking who you are, and it is deliberate.
- **Adding a second passkey to an existing account is not built yet.** Requests can now prove
  which account they belong to, so the thing that blocked it is gone — but the endpoint itself is
  still to come. Until then an account has exactly the one passkey it was opened with, which is
  worth knowing before you rely on it: lose that authenticator and the account is unreachable.

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

The overlay is what publishes Postgres on `localhost:5432`; the `Development` settings files point
the API and Worker there. Outside compose, and outside `Development`, both services need
`ConnectionStrings__Core` set — they refuse to start without it rather than failing on the first
request that needs a database.

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
| App authenticates to OpenBao with a **root** token | No app code calls Transit yet | A scoped policy granting only `transit/encrypt` + `transit/decrypt` on its own keys |
| `tls_disable = true` on the OpenBao listener | Traffic never leaves the compose network | TLS terminated at the listener, or a mutually-authenticated mesh |
| `docker-compose.dev-ports.yml` publishing Postgres/RabbitMQ/OpenBao | You need psql and the UIs | Never used. Backing services stay off any public interface |
| Migrations run automatically at startup | One machine, one instance | An explicit pre-deploy step — multiple replicas racing to migrate is exactly what that avoids |
| Passkey relying party is `localhost` over plain HTTP | It is the only origin your browser will reach | Your real domain over HTTPS. Browsers refuse WebAuthn on any non-`localhost` origin without it, so this is enforced whether you set it or not |
| Token signing key `dbr_dev_token_signing_key_...`, committed to the repo | Nobody else can reach the API to use it | A real secret. Whoever knows this key can mint an access token for any account, so it is the single most important value in this table |

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
