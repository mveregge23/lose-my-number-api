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

`openbao-init` exiting is normal and expected. `api` and `worker` wait for it to finish
successfully before they start.

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

**RabbitMQ or Postgres seems to have lost its data** — check you didn't run `docker compose down -v`
or `docker volume prune -a`. Note that a volume being present is not proof state is being kept;
verify with a restart test.

**`OpenBao has dropped support for mlock`** — OpenBao 2.x removed mlock and refuses to start if
`disable_mlock` appears in its config at all. Don't add it back.

## Contributing

Contributions are welcome. Two conventions worth knowing up front:

- **Commits need a `Signed-off-by:` trailer** (DCO, `git commit -s`). There's no CLA.
- **If your change affects how another developer runs this locally, update this README in the same
  PR.** New service, changed port, new required tool, new setup step, changed reset procedure — if
  someone with a fresh clone would hit it, it belongs here. A README that drifts from the compose
  file is worse than no README, because it fails at exactly the moment someone is trying to start.

Reviewer expectations vary by path (see `.github/CODEOWNERS`): broker recipes are reviewed as data,
while connectors, legal-basis content, and database migrations carry a higher bar.

## License

[GNU AGPL-3.0-or-later](LICENSE). The network-use clause is deliberate: running a modified version
as a public service obliges you to publish those modifications.
