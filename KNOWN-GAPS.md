<!--
SPDX-FileCopyrightText: 2026 Max Veregge
SPDX-License-Identifier: AGPL-3.0-or-later
-->

# Known gaps

Things a statute or the design requires that this codebase does not do yet, written down where
somebody who does not have the private backlog can find them and pick one up.

This is deliberately not a wish list. An entry earns its place by naming an obligation that already
exists — usually because a law in the catalog says so — and by being specific enough that somebody
could start on it without asking what was meant. Each one says what the requirement is, what the
code does today, and roughly what closing it would involve.

**Legal readings here are unreviewed**, exactly like the rows in `legal_basis`. They cite primary
sources so they can be checked, and nobody qualified has signed off on them.

## How to add one

Open a PR adding a section in the same shape as the others: what is required, what exists today,
what closing it involves, and a citation to whatever imposes the requirement. If you are closing a
gap rather than adding one, delete the section in the same PR that lands the work — a gaps file
that outlives its gaps stops being read.

---

## Appeals against a refused request

**Required by:** Connecticut, Virginia and Colorado all oblige a controller to run an appeals
process for a request it declines. Connecticut is the most explicit: a controller must "establish a
process for a consumer to appeal the controller's refusal to take action on a request", the process
must be "conspicuously available and similar to the process for submitting requests", and "not later
than sixty days after receipt of an appeal" the controller must respond in writing with its reasons.
If the appeal is denied, the controller must also hand the consumer a way to complain to the
Attorney General.
[PA 22-15 § 4(d)](https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF)

A refusal is not silent, either: under § 4(c)(2) a controller that declines must say so "not later
than forty-five days after receipt of the request", giving "the justification for declining to take
action and instructions for how to appeal the decision". So a refused request arrives with both a
reason and a route already attached to it.

**Today:** nothing models a refusal, so nothing models an appeal. A removal request has no terminal
state that means "the broker said no and gave a reason", and there is no record of a tenant
contesting one. The sixty-day appeal clock exists in none of the schema — `legal_basis` carries a
response deadline and an extension, and neither is this.

**What closing it involves, roughly:** a refusal has to become first-class before an appeal can be —
a status on the removal request, the broker's stated reason, and the date it arrived. Then the
appeal itself is a second clock against the same request, with its own deadline drawn from the
governing regime rather than from the broker's pacing. Two things worth deciding early: whether an
appeal is a new request or a state of the existing one (the statutes read as the latter), and
whether the system files appeals on the tenant's behalf at all or simply tells them the right
exists, when it exists, and how long the broker then has. The second is a product question, not a
schema one, and it is the one that decides how much of the rest is needed.

**Where it would live:** the removal request lifecycle, so it depends on that existing first. The
deadline itself is catalog data and would want a column on `legal_basis` beside the response
deadline — at which point the appeal window is subject to the same `deadline_unit` question the
response window already answers.

---

## The catalog models three request types; the statutes grant five

**Required by:** Connecticut enumerates five consumer rights — confirm whether a controller is
processing your data and access it, correct inaccuracies, delete, obtain a portable copy, and opt
out of targeted advertising, sale or profiling. The other four states in the catalog grant a
similar set. One response clock in § 4(c)(1) covers a request to exercise any of them.
[PA 22-15 § 4(a)](https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF)

**Today:** `LegalRequestType` has three values — `delete`, `opt_out_sale`,
`opt_out_targeted_ads` — so a jurisdiction's rows can only ever describe those. Confirmation and
access, correction, and portability have no representation, and neither does opting out of
profiling, which Connecticut folds into the same subdivision as the two opt-outs the catalog does
carry.

**What closing it involves, roughly:** widening the enum is the easy half — a migration widening the
check constraint on `legal_basis.request_type`, a member, a spelling in `CatalogVocabulary`, and
rows per jurisdiction. The question worth settling first is which of them this product should
actually exercise on somebody's behalf.

**Confirmation is the interesting one.** The right to confirm whether a broker is processing your
data is, in substance, the statutory version of what a scan does by scraping — and it comes with a
legal obligation to answer rather than a page that may or may not be parseable. A removal pipeline
that could ask the question directly, in writing, with a deadline attached, would have firmer
ground under it than one inferring presence from search results. It also has a cost a scan does
not: it tells the broker exactly who is asking, which is a disclosure to weigh rather than assume.

---

## Brokers are not synced from files yet

**Required by:** the design puts broker recipes in `/catalog/brokers/*.yaml` alongside legal-basis
content, and `.github/CODEOWNERS` already reserves that path at the one-approval tier — a lower bar
than legal content, because a bad recipe fails one broker's jobs while a bad statute misinforms
somebody about their rights.

**Today:** `catalog-sync` reads `catalog/legal-basis/**` and nothing else. Broker rows are still
inserted by hand or by migration, and `broker` has no `source` column, so nothing distinguishes a
row the shared catalog would own from one an operator entered.

**What closing it involves, roughly:** the mechanism is built and the shape is the same — a file
schema, a validator, `source` on the table, and the same upsert-and-retract pass. Two things differ.
A broker's pacing fields are exactly the kind of thing an operator may want to tune locally, so the
all-or-nothing ownership that suits a statute may be too coarse here. And `broker_legal_basis` — the
confirmations that a regime governs a company — is curated content too, currently modelled nowhere
in the files; it is what makes retracting a regime fail today, so whichever story adds it should
decide whether a confirmation lives in the broker's file, the regime's, or its own.

This was left out deliberately rather than missed: no broker rows ship at all, so a sync for them
would have had no content to apply and nothing to test against beyond fixtures.

---

## The catalog has no companies in it

**Required by:** §6.3 makes the broker catalog the reference data every scan and removal request
resolves against, and §9.1 expects it to reach the hundreds. `broker` is where a company's domain,
opt-out method, courtesy SLA and pacing live, and `broker_legal_basis` is where somebody records
that a given statute reaches a given company.

**Today:** both tables are empty, and nothing fills them. There is no `catalog/brokers/` directory,
no migration inserts a broker row, and `catalog-sync` reads legal-basis content only. Fifteen
legal-basis rows ship; zero companies do.

This is worth spelling out because the rest of the system looks finished around it. Concretely:

- A scan that is not narrowed means "the whole catalog", which is nothing. It completes and finds
  nothing, and that is indistinguishable from a clean bill of health.
- The per-broker queue lanes declare **zero endpoints** — the lane directory reads active brokers
  from an empty table. The pacing works; there is nothing to pace.
- A removal request has no company to be addressed to.
- With no `broker_legal_basis` rows, jurisdiction resolution always falls back to the broker's
  operational default, so **no statutory deadline is reachable at all** however many jurisdictions
  get seeded. The deadline machinery is complete and permanently on its fallback path.

**What closing it involves, roughly:** three separate kinds of work. A broker sync — file schema,
validator, a `source` column on `broker`, and the same upsert-and-retract pass legal-basis content
gets, with the wrinkle that pacing fields are what an operator would most want to tune locally, so
all-or-nothing ownership may be too coarse. A decision about where a `broker_legal_basis`
confirmation lives — the broker's file, the regime's, or its own — which is what makes retracting a
regime fail today. And then the content itself: domain, removal method, opt-out URL, SLA, contact
mode and pacing, read off real sites and cited, which is research rather than programming and is the
long pole.

Fixtures stand in for a company in tests, so this does not block building the search or the
connectors. It blocks anything running against a real one.

---

## Nothing searches a broker, and nothing was going to

**Required by:** §6.4 makes a scan the act of asking a set of brokers what they hold about one
identity, and §2 draws a scan worker as its own box alongside the removal worker. `Exposure` exists
to hold what it finds, down to a `confidence` score for how sure the match is.

**Today:** no code searches anything, and — until this entry was written — no plan did either. The
design specifies the removal half in detail and never specifies the search half. §9's connector
contract is not it: `ConnectorContext` carries a `RemovalRequestId` and a `SourceRef` read off an
existing exposure, so a connector consumes exposures and cannot produce one. Nothing computes
`Exposure.confidence`. A `scan` row therefore reaches `queued` and stays there permanently, and
finishing the removal pipeline exactly as designed would not change that.

Everything around a search now exists and **nothing implements `IBrokerSearch`**. A queued run is
claimed, fanned out to one leg per company through that company's own lane, given a grant covering
exactly the groups its search declared, and settled when every leg has answered; what each leg finds
is scored against one floor and written as an exposure if it clears. The registry those legs resolve
against is empty, so every leg finishes as `no_search_available` and every run finishes having found
nothing. **The pipeline is real and it searches nobody.**

**What closing it involves, roughly:** a generic engine that interprets a search recipe — a
declarative document naming the placeholders it needs and how to read a result page — plus the
recipes themselves, reviewed as data rather than code at the §9.1 recipe bar. A code tier for the
handful of companies a document cannot describe, allow-listed and compiled in rather than
discovered. And a registry that resolves a catalog row to one of them, replacing the empty one:
that is a registration, because both sides of a leg already resolve through the same interface.

**Worth being blunt about:** this is the product's first half, and what is missing is now the
narrow part rather than the shape of it. Everything else in the repository — the catalog, consent,
jurisdiction resolution, the tenant boundary, the exposure surface, the monthly schedule, and now
the scan pipeline itself — is real and tested, and none of it finds anything, because nothing knows
how to read a broker's website. Do not read a completed scan with no findings as good news yet.

---

## A worker cannot report how a job ended

**Required by:** §6.7 defines three worker-facing routes behind mutual TLS. One of them — the
job-scoped vault release — now exists. The other two are the callbacks a worker uses to say that a
job succeeded, with a reference to whatever evidence it got, or failed, with a reason code. §5's
lifecycle depends on them: `Submitted` moves on only when something reports what happened.

**Today:** the internal listener is there, with mutual TLS and a route table the public edge does
not share, and `POST /internal/v1/vault/release` runs on it. Nothing reports outcomes, because
there is no job model to report about — the removal request and its jobs are a later phase, and a
callback shaped against an imagined job record is the objection that deferred the scoped release
the first time round.

**What closing it involves, roughly:** two routes on the listener that already exists, which is the
small half. The larger half is deciding what a worker is trusted to assert. The release route
answers to a grant that names one leg of one scan and is spent by being used; a completion callback
that merely named a job id would let anything holding a certificate mark any job done. The same
reasoning that made the release a capability applies, and the shape it wants is probably the same:
something minted with the work, presented once, and meaningful only for the job it was minted for.

**Also still open on the edge itself:** revocation. The listener trusts an authority and a common
name, and there is no revocation list, so withdrawing a worker's access today means reissuing the
authority and restarting both processes. That is written into the code beside the check rather than
left to be discovered, and it is the thing to fix first if this is ever run anywhere real.

---

## Verification scans are also unbuilt

**Required by:** §5's lifecycle has two transitions that only a scan can drive —
`AwaitingBrokerResponse → Removed` when a verification scan confirms a listing is gone, and
`Removed → Reappeared` when a later one finds it again. The second is the reason the design insists
removal is not fire-and-forget: brokers re-buy and re-scrape.

**Today:** neither transition has anything behind it. Without them a removal request can never reach
a terminal success state on evidence, and a listing that comes back is never noticed.

**What closing it involves, roughly:** the same worker as the gap above, pointed at an exposure that
already exists rather than at a fresh search, plus a decision about how long after a submission the
first verification is worth running — which §9's `Success(ReceiptRef, VerifyNotBefore)` already
anticipates a connector answering.

---

## An exposure cannot yet say what it matched

**Required by:** §3 gives `EXPOSURE` an `encryptedSourceRef` — the pointer to the specific broker
profile page a match was found on — and classes it Restricted-PII, which puts it in the vault store
under field-level envelope encryption alongside names and addresses. §1's minimization rule then
requires purging it once the exposure is `removed` and its verification window has passed.

**Today:** the `exposure` table has no such column, in either store — and scans now write exposures,
so this has stopped being theoretical. A search hands back the listing it found; the leg scores the
candidate, writes the finding, and **drops the pointer on the floor**. Two findings on one company
are therefore told apart by their confidence and by nothing else, and there is no way to go back and
look at what was found.

**What closing it involves, roughly:** a vault-side table keyed by exposure id, in the same shape as
`vault.profile_identity`, plus a release path for whatever needs to read it and the purge that
minimization requires. It was left out rather than stubbed because the alternative was a column on
the core table, and a nullable `bytea` sitting in `public.exposure` is exactly the kind of thing a
later story fills in without noticing which store it is in — which is the whole distinction the
vault exists to hold.

---

## Business-day deadlines need a holiday calendar

**Required by:** California counts its opt-out compliance window in business days — "no later than
15 business days from the date the business receives the request" — and `legal_basis.deadline_unit`
records that faithfully.
[CPPA published text, subd. (f)(1)](https://cppa.ca.gov/regulations/pdf/ccpa_statute_eff_20260101.pdf)

**Today:** `DeadlineCalculator` turns a count into a date and skips weekends, so a fifteen
business-day window from a Monday lands on the Monday three weeks later rather than the Tuesday a
fortnight out. **Public holidays are not skipped.** A window crossing one lands a day early, and
early is the wrong direction: it reports a request overdue while the recipient still has time, which
is the failure `deadlineSource` exists to prevent, arrived at by arithmetic instead of by labelling.

The error is bounded and small — at most a couple of days across a window of three weeks — which is
why this is a known gap rather than a reason to hold the resolver back. It matters most for the one
rule in the catalog counted this way, California's opt-out clock.

**What closing it involves, roughly:** a source of holidays for the jurisdiction a deadline is
governed by, and a decision about whose holidays count when the tenant and the broker sit in
different states. Worth resisting the urge to pull in a general-purpose holiday library for one
statute; two jurisdictions' worth of dates in the catalog may be the smaller, more reviewable thing.

---

## Consent revocation has its own clock, and nothing carries it

**Required by:** Connecticut requires a controller to provide a mechanism to revoke consent and,
once revoked, to "cease to process the data as soon as practicable, but not later than fifteen days
after the receipt of such request".
[PA 22-15 § 6(a)(6)](https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF)

**Today:** not modelled, and deliberately not filed under opt-outs. Revoking consent withdraws a
permission the consumer previously gave; opting out is a right exercised against processing that
never needed permission. The catalog's three request types cover deletion and the two opt-outs, and
none of them is revocation.

**What closing it involves, roughly:** first a decision about whether it belongs here at all. This
system asks brokers to delete and to stop selling; whether it also tracks consent a tenant gave a
broker directly is a scope question. If it does, revocation is a fourth request type with its own
deadline, and the fifteen days becomes an ordinary catalog row.
