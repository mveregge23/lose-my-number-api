-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The broker catalog and the legal regimes it is confirmed against.
--
-- Three tables and one idea: this is reference data shared by every tenant, curated by
-- whoever operates the instance, and read on nearly every request that goes anywhere
-- near a removal. It is the first data in this schema that belongs to nobody.
--
-- ---------------------------------------------------------------------------------
-- Why these sit outside the tenant boundary
-- ---------------------------------------------------------------------------------
--
-- Every table so far has either opted into row-level security or explained why it
-- could not. These do not opt in, and the reason is not that scoping them is hard —
-- it is that there is nothing to scope them to. A broker is a company; a legal basis
-- is a statute. Neither belongs to an account, and a policy comparing a tenant id
-- against a column that does not exist would fail at query time rather than protect
-- anything.
--
-- The thing to be careful about is the opposite mistake from the usual one. Elsewhere
-- the danger is a table that should be scoped and is not. Here it is a table that is
-- shared and gets scoped anyway: every tenant would see an empty catalog, every
-- removal would fall back to an operational deadline, and nothing would look broken
-- except the answers. That is why the tests for these assert that two different
-- tenants, and a connection carrying no tenant at all, all read the same rows.
--
-- None of this holds PII. A broker's name, domain and opt-out method are public facts
-- about a company, and a legal basis is a public fact about a law.
--
-- ---------------------------------------------------------------------------------
-- Why the application may read this and not write it
-- ---------------------------------------------------------------------------------
--
-- dbr_app is granted SELECT and nothing else. The catalog is reviewed content: a
-- broker recipe that is wrong fails a job and gets retried, but a legal basis row with
-- the wrong deadline quietly misinforms somebody about their actual legal position.
-- Curated writes arrive from the catalog sync at deploy time, which runs with the
-- privileges migrations run with — so the request-serving role never needs them, and
-- the code that computes a statutory deadline provably cannot edit the statute it
-- computed from.

CREATE TABLE broker (
    id                    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    name                  text        NOT NULL
                                      CONSTRAINT broker_name_present
                                      CHECK (length(btrim(name)) > 0),

    -- The identity a listing is actually found under, and the reason this is unique:
    -- two catalog rows for one company would pace it as two lanes and let it be sent
    -- twice for the same person.
    domain                text        NOT NULL
                                      CONSTRAINT broker_domain_unique UNIQUE,

    -- Text with a check rather than a Postgres enum, as elsewhere: a value added to an
    -- enum type cannot be removed again, and this list is expected to be revisited.
    removal_method        text        NOT NULL
                                      CONSTRAINT broker_removal_method_known
                                      CHECK (removal_method IN ('webform', 'email', 'api', 'postal')),

    -- What a removal is given when no statute applies. Deliberately not a deadline in
    -- its own right: which of the two a request got is recorded on the request, so
    -- somebody reading a date can tell a legal guarantee from a courtesy target.
    sla_days              integer     NOT NULL
                                      CONSTRAINT broker_sla_days_positive
                                      CHECK (sla_days > 0),

    active                boolean     NOT NULL DEFAULT true,

    -- Null until something has actually checked this entry against the live site.
    -- A row that has never been verified and one verified long ago are different
    -- problems, and a default of now() would erase the difference at insert.
    catalog_verified_at   timestamptz NULL,

    -- Pacing, per broker rather than global, because a company known to be twitchy
    -- about automated traffic earns a stricter lane than one that has never minded.
    --
    -- The defaults are the slowest lane there is: one job at a time, a second between
    -- them. A catalog row added without thinking about pacing should be gentler than
    -- the operator intended rather than more aggressive — the failure mode of guessing
    -- high is this service looking like something a broker should block.
    max_concurrency       integer     NOT NULL DEFAULT 1
                                      CONSTRAINT broker_max_concurrency_positive
                                      CHECK (max_concurrency > 0),

    min_delay_ms          integer     NOT NULL DEFAULT 1000
                                      CONSTRAINT broker_min_delay_not_negative
                                      CHECK (min_delay_ms >= 0),

    -- How many consecutive rate-limit answers open the breaker, and how long it stays
    -- open. Both live here rather than in application config for the same reason the
    -- pacing does: they are facts about one company.
    rate_limit_threshold  integer     NOT NULL DEFAULT 3
                                      CONSTRAINT broker_rate_limit_threshold_positive
                                      CHECK (rate_limit_threshold > 0),

    cooldown_minutes      integer     NOT NULL DEFAULT 30
                                      CONSTRAINT broker_cooldown_minutes_positive
                                      CHECK (cooldown_minutes > 0),

    -- How many consecutive "the form changed" answers flag the entry for review.
    form_change_threshold integer     NOT NULL DEFAULT 3
                                      CONSTRAINT broker_form_change_threshold_positive
                                      CHECK (form_change_threshold > 0),

    -- Whether this broker will accept an alias address for correspondence or insists
    -- on the one it already has on file. It decides whether contacting them costs the
    -- person another disclosure of an address the broker already holds.
    email_contact_mode    text        NOT NULL DEFAULT 'alias_preferred'
                                      CONSTRAINT broker_email_contact_mode_known
                                      CHECK (email_contact_mode IN ('alias_preferred', 'tenant_real_required')),

    created_at            timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE broker IS
    'Data broker catalog. Shared reference data owned by no tenant, read on nearly '
    'every removal path, and writable only by whatever applies the curated catalog.';

COMMENT ON COLUMN broker.sla_days IS
    'Operational default only. A request governed by a statute takes its deadline from '
    'the legal basis instead, and records which of the two it used.';

-- Dispatch and the public listing both ask for active brokers.
CREATE INDEX broker_active ON broker (active) WHERE active;

CREATE TABLE legal_basis (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The regime, as it is cited: CCPA, GDPR, VCDPA.
    code                   text        NOT NULL
                                       CONSTRAINT legal_basis_code_present
                                       CHECK (length(btrim(code)) > 0),

    request_type           text        NOT NULL
                                       CONSTRAINT legal_basis_request_type_known
                                       CHECK (request_type IN ('delete', 'opt_out_sale', 'opt_out_targeted_ads')),

    -- Who the regime protects, in the same coarse shape a profile records residency
    -- in. The two are compared directly when a request works out which regimes apply,
    -- so a second spelling here would silently match nothing.
    residency_scope        text        NOT NULL
                                       CONSTRAINT legal_basis_residency_scope_coarse
                                       CHECK (residency_scope ~ '^[A-Z]{2}(-[A-Z0-9]{1,3})?$'),

    response_deadline_days integer     NOT NULL
                                       CONSTRAINT legal_basis_response_deadline_positive
                                       CHECK (response_deadline_days > 0),

    -- A one-time extension where the regime allows one. Zero means it does not, which
    -- is a different statement from "nobody filled this in" — hence NOT NULL.
    extension_days         integer     NOT NULL DEFAULT 0
                                       CONSTRAINT legal_basis_extension_days_not_negative
                                       CHECK (extension_days >= 0),

    verification_level     text        NOT NULL
                                       CONSTRAINT legal_basis_verification_level_known
                                       CHECK (verification_level IN ('none', 'basic', 'enhanced')),

    -- Provenance, and required rather than optional. A row here decides what somebody
    -- is told about their legal position; one that cannot say which primary source it
    -- came from or who read it is worse than an absent row, because an absent row
    -- falls back to an operational deadline that is honestly labelled as one.
    citation_url           text        NOT NULL
                                       CONSTRAINT legal_basis_citation_present
                                       CHECK (length(btrim(citation_url)) > 0),

    reviewed_at            timestamptz NOT NULL,

    reviewed_by            text        NOT NULL
                                       CONSTRAINT legal_basis_reviewer_present
                                       CHECK (length(btrim(reviewed_by)) > 0),

    created_at             timestamptz NOT NULL DEFAULT now(),

    -- One row per regime, request type and scope. Two rows differing only in deadline
    -- would make which one governs a request a matter of which the planner returned.
    CONSTRAINT legal_basis_one_per_scope UNIQUE (code, request_type, residency_scope)
);

COMMENT ON TABLE legal_basis IS
    'Reviewed legal regimes: who a statute protects, what it requires, and where that '
    'was read. Curated by the operator and counsel, never by the application.';

-- Resolution starts from where somebody lives.
CREATE INDEX legal_basis_residency_scope ON legal_basis (residency_scope);

-- Which regimes a given broker is actually subject to. Deliberately a table somebody
-- fills in rather than something computed: applicability turns on revenue and
-- data-volume thresholds this system has no way to verify, so a confident guess would
-- be a guess presented to a tenant as a legal position.
CREATE TABLE broker_legal_basis (
    -- A broker leaving the catalog takes its confirmations with it: they are claims
    -- about that company and mean nothing without it.
    broker_id      uuid        NOT NULL REFERENCES broker (id) ON DELETE CASCADE,

    -- A legal basis does not go quietly. Removing a statute that brokers are confirmed
    -- against is refused, because the rows pointing at it are the reviewed judgement
    -- that it applies — losing them silently is how a removal quietly downgrades to an
    -- operational deadline with nobody noticing.
    legal_basis_id uuid        NOT NULL REFERENCES legal_basis (id) ON DELETE RESTRICT,

    confirmed_at   timestamptz NOT NULL DEFAULT now(),

    confirmed_by   text        NOT NULL
                               CONSTRAINT broker_legal_basis_confirmer_present
                               CHECK (length(btrim(confirmed_by)) > 0),

    PRIMARY KEY (broker_id, legal_basis_id)
);

COMMENT ON TABLE broker_legal_basis IS
    'Admin-confirmed applicability: this regime governs this broker, confirmed by this '
    'person on this date. Never inferred.';

-- The reverse lookup — which brokers a regime covers — for the admin surface, since
-- the primary key only serves broker-first reads.
CREATE INDEX broker_legal_basis_legal_basis_id ON broker_legal_basis (legal_basis_id);

-- Read-only for the role that serves requests, and no call to app.enable_tenant_rls:
-- see the note at the top of this file for both.
GRANT SELECT ON broker, legal_basis, broker_legal_basis TO dbr_app;
