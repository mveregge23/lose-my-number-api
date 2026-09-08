#!/bin/bash
# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# One-shot bootstrap for Postal. Idempotent: safe to run on every `docker compose
# up`. It issues the signing key on first run, creates and migrates the schema,
# and makes sure the admin user, organisation, mail server and SMTP credential
# this stack sends through all exist.
#
# The last part is the reason this script is longer than it looks like it should
# be. Postal's own CLI can create a schema and an admin user and stops there — an
# organisation, a mail server and a credential are made through the web interface,
# which is a reasonable thing to ask of somebody standing up a mail server and a
# poor thing to ask of somebody running `docker compose up` to try this project.
# So the rest is driven through Postal's Rails console, which is the same code the
# interface calls.
#
# THIS IS A LOCAL-DEVELOPMENT CONVENIENCE. It fixes the SMTP credential to a value
# from the environment so the worker can be configured before Postal exists, which
# means the credential is whatever is in .env — fine on a laptop and wrong anywhere
# a network can reach. A real deployment issues the credential in the interface and
# puts it in the worker's configuration itself.

set -eu

SIGNING_KEY_PATH="${POSTAL_SIGNING_KEY_PATH:-/config/signing.key}"

ORG_NAME="${POSTAL_ORG_NAME:-Removals}"
ORG_PERMALINK="${POSTAL_ORG_PERMALINK:-removals}"
SERVER_NAME="${POSTAL_SERVER_NAME:-Demands}"
SERVER_PERMALINK="${POSTAL_SERVER_PERMALINK:-demands}"
SERVER_MODE="${POSTAL_SERVER_MODE:-Live}"

ADMIN_EMAIL="${POSTAL_ADMIN_EMAIL:-admin@example.com}"
ADMIN_PASSWORD="${POSTAL_ADMIN_PASSWORD:-postal_dev_password}"

# The key the worker authenticates with. Postal looks a credential up by this
# value alone — the SMTP username is not consulted at all — so this is the whole
# of the secret.
SMTP_KEY="${POSTAL_SMTP_KEY:?POSTAL_SMTP_KEY is required: it is the credential the worker sends with}"

# The domain demands are sent from, and the one replies come back to.
MAIL_DOMAIN="${POSTAL_MAIL_DOMAIN:?POSTAL_MAIL_DOMAIN is required}"

# Postal's own processes run as uid 999, and a named volume is created owned by
# root — so this one-shot runs as root to be able to write the key at all, and hands
# the directory back before it exits. Without the hand-off the relay would start and
# then fail to read the key it needs to sign with, which is a worse failure than not
# starting: the SMTP server would accept demands it could never deliver.
POSTAL_UID=999
POSTAL_GID=999

if [ ! -f "$SIGNING_KEY_PATH" ]; then
  echo "postal-init: issuing a signing key at $SIGNING_KEY_PATH"
  mkdir -p "$(dirname "$SIGNING_KEY_PATH")"
  openssl genrsa -out "$SIGNING_KEY_PATH" 2048
  chmod 600 "$SIGNING_KEY_PATH"
fi

chown -R "$POSTAL_UID:$POSTAL_GID" "$(dirname "$SIGNING_KEY_PATH")"

echo "postal-init: creating and migrating the schema"
postal initialize

echo "postal-init: reconciling the organisation, mail server and credential"

# Everything below is written to be re-runnable: each object is found before it is
# created, so a second `compose up` changes nothing. The credential's key is forced
# with update_column rather than assigned, because Postal generates a random key in
# a before_validation hook on create and refuses to let it be changed afterwards —
# which is right for a mail server operated through its interface, and is the one
# thing in the way of a stack that configures itself.
postal console <<'RUBY'
org_permalink   = ENV.fetch("POSTAL_ORG_PERMALINK", "removals")
org_name        = ENV.fetch("POSTAL_ORG_NAME", "Removals")
srv_permalink   = ENV.fetch("POSTAL_SERVER_PERMALINK", "demands")
srv_name        = ENV.fetch("POSTAL_SERVER_NAME", "Demands")
srv_mode        = ENV.fetch("POSTAL_SERVER_MODE", "Live")
admin_email     = ENV.fetch("POSTAL_ADMIN_EMAIL", "admin@example.com")
admin_password  = ENV.fetch("POSTAL_ADMIN_PASSWORD", "postal_dev_password")
smtp_key        = ENV.fetch("POSTAL_SMTP_KEY")
mail_domain     = ENV.fetch("POSTAL_MAIL_DOMAIN")

user = User.find_by(email_address: admin_email)
if user.nil?
  user = User.new(
    first_name: "Postal",
    last_name: "Admin",
    email_address: admin_email,
    password: admin_password,
    password_confirmation: admin_password,
    admin: true,
    email_verified_at: Time.now
  )
  user.save!
  puts "postal-init: created admin #{admin_email}"
end

org = Organization.find_by(permalink: org_permalink)
if org.nil?
  org = Organization.new(name: org_name, permalink: org_permalink, time_zone: "UTC", owner: user)
  org.save!
  puts "postal-init: created organisation #{org_permalink}"
end

unless org.organization_users.exists?(user: user)
  org.organization_users.create!(user: user, admin: true)
end

server = org.servers.find_by(permalink: srv_permalink)
if server.nil?
  server = org.servers.build(name: srv_name, permalink: srv_permalink, mode: srv_mode)
  server.save!
  puts "postal-init: created mail server #{org_permalink}/#{srv_permalink}"
end

# The domain has to be present and verified for Postal to accept mail claiming to
# be from it. Verified outright here: DNS verification needs records this stack
# cannot publish for a domain nobody owns, and refusing to send until it passes
# would make a local stack unable to demonstrate the thing it exists to
# demonstrate. A real deployment verifies it properly, which is what the README
# describes.
domain = server.domains.find_by(name: mail_domain)
if domain.nil?
  domain = server.domains.build(name: mail_domain, verification_method: "DNS")
  domain.verified_at = Time.now
  domain.save!
  puts "postal-init: added and verified #{mail_domain}"
elsif domain.verified_at.nil?
  domain.update_column(:verified_at, Time.now)
end

credential = server.credentials.find_by(type: "SMTP", name: "worker")
if credential.nil?
  credential = server.credentials.build(type: "SMTP", name: "worker")
  credential.save!
  puts "postal-init: created the worker's SMTP credential"
end

if credential.key != smtp_key
  credential.update_column(:key, smtp_key)
  puts "postal-init: set the worker's SMTP credential to the configured value"
end
RUBY

echo "postal-init: done"
