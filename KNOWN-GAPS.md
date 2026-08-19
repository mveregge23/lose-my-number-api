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

## Business-day deadlines need a holiday calendar

**Required by:** California counts its opt-out compliance window in business days — "no later than
15 business days from the date the business receives the request" — and `legal_basis.deadline_unit`
records that faithfully.
[CPPA published text, subd. (f)(1)](https://cppa.ca.gov/regulations/pdf/ccpa_statute_eff_20260101.pdf)

**Today:** the unit is stored and published on the API, and nothing computes a date from it yet.
When something does, weekends fall out of arithmetic but public holidays do not, and a business-day
count that ignores them lands early — which is the direction that tells somebody a request is
overdue while the recipient still has time.

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
