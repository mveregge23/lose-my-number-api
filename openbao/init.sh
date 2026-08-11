#!/bin/sh
# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# One-shot bootstrap for a persistent (non-dev) OpenBao. Idempotent: safe to run
# on every `docker compose up`. It initializes the barrel on first run, unseals
# it on every run, and makes sure the Transit engine and the well-known local
# token exist.
#
# THIS IS A LOCAL-DEVELOPMENT CONVENIENCE. It writes the unseal key and root
# token to a file so the stack comes up without a manual unseal step. That trade
# is fine on a laptop and wrong everywhere else — a real deployment unseals via
# a KMS/HSM auto-unseal backend or a human with a split key, and never keeps the
# unseal key next to the data it protects.

set -eu

BAO_ADDR="${BAO_ADDR:-http://openbao:8200}"
export BAO_ADDR

KEYFILE=/keys/openbao-init.txt
DEV_TOKEN="${DEV_TOKEN:-dbr_dev_root_token}"

# `bao status` exits 0 unsealed, 2 sealed, 1 when it can't reach the server.
# Anything but 1 means the server is answering.
wait_for_server() {
  i=0
  while [ "$i" -lt 60 ]; do
    bao status >/dev/null 2>&1 && return 0
    [ $? -eq 2 ] && return 0
    i=$((i + 1))
    sleep 1
  done
  echo "openbao-init: server never became reachable at $BAO_ADDR" >&2
  return 1
}

is_initialized() { bao status 2>/dev/null | grep -qi '^Initialized[[:space:]]*true'; }
is_sealed()      { bao status 2>/dev/null | grep -qi '^Sealed[[:space:]]*true'; }

wait_for_server

# First run, or the data volume was cleared with `down -v`. A stale keyfile from
# a previous barrel is useless against a fresh one, so it gets overwritten.
if ! is_initialized; then
  echo "openbao-init: uninitialized barrel — initializing"
  bao operator init -key-shares=1 -key-threshold=1 > "$KEYFILE"
  chmod 600 "$KEYFILE"
fi

if [ ! -s "$KEYFILE" ]; then
  echo "openbao-init: barrel is initialized but $KEYFILE is missing." >&2
  echo "  The unseal key is unrecoverable, so the data in the openbao-data" >&2
  echo "  volume cannot be read. Run 'docker compose down -v' to discard it" >&2
  echo "  and start from an empty barrel." >&2
  exit 1
fi

UNSEAL_KEY=$(awk '/^Unseal Key 1:/ {print $NF}' "$KEYFILE")
ROOT_TOKEN=$(awk '/^Initial Root Token:/ {print $NF}' "$KEYFILE")

if [ -z "$UNSEAL_KEY" ] || [ -z "$ROOT_TOKEN" ]; then
  echo "openbao-init: could not parse $KEYFILE" >&2
  exit 1
fi

if is_sealed; then
  echo "openbao-init: unsealing"
  bao operator unseal "$UNSEAL_KEY" >/dev/null
fi

BAO_TOKEN="$ROOT_TOKEN"
export BAO_TOKEN

# Transit is what IKeyManagementProvider (DBR-015) talks to. Creating it here
# rather than in app startup keeps the app from needing root-level privileges.
if bao secrets list 2>/dev/null | grep -q '^transit/'; then
  echo "openbao-init: transit already enabled"
else
  echo "openbao-init: enabling transit"
  bao secrets enable transit >/dev/null
fi

# The generated root token changes on every re-init, which would mean editing
# .env each time. Minting an additional token with a stable, well-known id lets
# the API and Worker keep a fixed config value across re-initializations.
if bao token lookup "$DEV_TOKEN" >/dev/null 2>&1; then
  echo "openbao-init: local token already present"
else
  echo "openbao-init: creating well-known local token"
  bao token create -id="$DEV_TOKEN" -policy=root -ttl=0 >/dev/null
fi

echo "openbao-init: ready"
