-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The identifying half of a privacy profile: the names, addresses, contact details and
-- date of birth a broker match actually keys off, and the wrapped key that turns them
-- back into something readable.
--
-- Every value here is ciphertext produced under a data key of this row's own, which is
-- itself stored only in wrapped form — the key that unwraps it never leaves the key
-- manager. A dump of this table is therefore ciphertext plus wrapped keys, and neither
-- half is worth anything without a service that is not in the dump.
--
-- Four ciphertext columns rather than one blob. A worker sent to fill in a broker's
-- opt-out form is given only the fields that form asks for, and a single encrypted
-- document would mean decrypting a date of birth in order to release a name. The
-- columns are the granularity the release path needs.
--
-- No foreign key to public.privacy_profile, deliberately. The reference is real and
-- the id is the same, but a foreign key is a dependency between the two stores, and
-- this schema exists in order to be movable to a database of its own. A constraint
-- that has to be dropped to make that move is a constraint that will be discovered on
-- the day of the move. Nothing here is reachable except by a caller that already
-- resolved the profile row through the tenant boundary on the other side.

CREATE TABLE vault.profile_identity (
    -- The profile this belongs to, and its key: one identity, one row, the same id on
    -- both sides of the boundary.
    privacy_profile_id  uuid        PRIMARY KEY,

    -- Carried here as well, because the boundary is enforced independently in each
    -- store. Resolving the owner by looking it up in the other one would mean this
    -- store trusting a table it deliberately cannot read.
    tenant_id           uuid        NOT NULL,

    -- The data key that encrypted the columns below, as the key manager returned it.
    -- Opaque: whatever the configured provider produced, stored verbatim and handed
    -- back unexamined, so a different provider is a configuration change rather than a
    -- migration.
    wrapped_data_key    text        NOT NULL,

    encrypted_names     bytea       NOT NULL,
    encrypted_addresses bytea       NOT NULL,
    encrypted_contacts  bytea       NOT NULL,

    -- Nullable because a profile without a date of birth is ordinary — brokers match
    -- on it where it is known, and nothing requires it. An empty ciphertext would say
    -- the same thing while costing a decryption to find out.
    encrypted_dob       bytea       NULL,

    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE vault.profile_identity IS
    'Envelope-encrypted identity fields for one privacy profile. Written and read only '
    'by the profile service, and released to workers a field at a time.';

COMMENT ON COLUMN vault.profile_identity.wrapped_data_key IS
    'This row''s data key, encrypted by the tenant''s wrapping key. Destroying that '
    'wrapping key makes every column beside it permanently unreadable, including in '
    'backups.';

CALL app.enable_tenant_rls('vault.profile_identity', 'tenant_id', 'dbr_vault');
