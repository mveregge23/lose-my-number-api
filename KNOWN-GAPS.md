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

## The catalog has one company in it

**Required by:** §6.3 makes the broker catalog the reference data every scan and removal request
resolves against, and §9.1 expects it to reach the hundreds.

**Today:** one. `catalog/brokers/spokeo/` is a real company with a row, a mailbox, a search recipe
and a CCPA confirmation citing the California registry, and it is the whole catalog. A scan that is
not narrowed searches Spokeo; a removal request can be addressed to Spokeo; a Californian's demand
to Spokeo gets a statutory deadline. Everyone else on the internet is not here.

**What closing it involves, roughly:** content, and a way to make it faster. The California
registry is a CSV of 603 registrants, each with a website, a contact mailbox, a privacy-rights URL
and self-reported deletion counts, and it is a primary source for the row and for the CCPA
confirmation alike — an importer that turns a registry export into `broker.yaml` files with the
registry as their citation is the difference between reading six hundred privacy pages and
reviewing six hundred diffs. It will need a rule for registrants that list several domains (63 of
them do), since a row here is one domain. What it cannot produce is a search recipe, which is read
off a real results page by a person and is the long pole per company — and see the entry below for
why most of those pages cannot be read at all yet.

---

## The recipe tier reaches almost nobody

**Required by:** §9.1's design has most companies described by a search recipe — a path, four
selectors, reviewed as data — and a hand-written class only for the handful a document cannot
describe. The recipe engine is an HTTP client and an HTML parser.

**Today:** of fourteen people-search sites probed on 2026-09-15 with an ordinary HTTP client and a
browser's User-Agent, **thirteen answered with a Cloudflare JavaScript challenge or a captcha** —
Whitepages, TruePeopleSearch, FastPeopleSearch, FamilyTreeNow, Nuwber, Radaris, ThatsThem,
BeenVerified, MyLife, SearchPeopleFree and the rest. A recipe against any of them finishes `blocked`
on the first request: correctly classified, finding nothing. Spokeo is the fourteenth, and it is the
first company in the catalog for exactly that reason.

The design's assumption was wrong about the shape of the web these companies run on, not about the
tier itself. The recipe — a reviewed document saying where to ask and what to read — is still the
right unit; what fetches the page has to be a browser.

**What closing it involves, roughly:** the one-job-one-process browser the removal side already
planned for web forms, used for search as well: a short-lived headless browser that loads the
results page, waits out the challenge where the challenge is the passive kind, and hands the
resulting document to the same recipe and the same selectors. Two things to be honest about before
building it. Cloudflare's managed challenge is designed to distinguish exactly this from a person,
and a headless browser passes it some of the time, not reliably; the outcome for a company that
does not pass stays `blocked`, and the catalog should be able to say per company which tier to use
so that a company known to block is not asked with the cheap tier first. And a browser process per
search is a different cost from an HTTP request per search — the pacing fields on the row were
written for the second, and the first may need its own.

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

## A listing that comes back does not reopen a demand

**Required by:** §5's lifecycle draws `Reappeared → Queued` for a listing found again after a
removal, gated on the tenant currently permitting `auto_resubmit` — the consent scope exists for
exactly this, and nothing else. The design insists removal is not fire-and-forget because brokers
re-buy and re-scrape; noticing the return is half of that, acting on it is the other half.

**Today:** the first half is built. A finished scan settles the demands waiting on the companies
it looked at: a listing found gone closes the demand as `removed`, a listing still present after
the deadline closes it as `failed`, and a listing that reappears is recognised by the digest stored
beside the finding and moves the demand to `reappeared`. There it stops. Nothing opens a fresh
demand, so `auto_resubmit` is a permission somebody can grant that no code path consults.

**What closing it involves, roughly:** the transition is `Reappeared → Queued` on the same row —
the demand is the thing that is retried, waited on and resubmitted, which is why a listing that
comes back returns to it rather than opening a second one. Re-queueing is the dispatcher's
business, not the verifier's: the path that records what a search saw should not also be the one
that puts a name in front of a company. So it is a small consumer of the reappearance, in the same
place the monthly scan already acts without a caller — re-read consent per run, refuse without it,
and move the demand back to `queued` so the lane picks it up as a fresh attempt.

---

## A finding is never purged

**Required by:** §1's minimization rule. The pointer to the listing a match was found on is
Restricted-PII and is kept only for as long as it is needed — once an exposure is `removed` and its
verification window has passed, there is nothing left to point at and the address should be gone.

**Today:** the address is stored, encrypted, in `vault.exposure_source`, and nothing deletes it. It
outlives the removal it justified.

One thing softens it and does not close it. Destroying a tenant's wrapping key on account deletion
makes every one of their findings permanently unreadable, including in backups — so **erasure works**
and it is retention that does not. The state the purge triggers on is reachable now: a finished scan
that finds nothing for a person at a company moves that company's findings to `removed`.

**What closing it involves, roughly:** a sweep over exposures that are `removed` with a verification
window behind them, deleting the vault row and leaving the finding's history — which is the point,
since the history is what stops a later scan re-offering the same listing as a fresh discovery. The
one decision it needs is how long that window is: a listing confirmed gone last week may come back
next month, and the digest that recognises its return lives beside the finding rather than in the
vault, so the purge does not cost that recognition. What it does cost is the ability to show the
person where it *was*, which is the trade the minimization rule asks for.

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
