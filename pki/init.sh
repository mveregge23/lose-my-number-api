#!/bin/sh
# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# One-shot certificate bootstrap for the internal edge. Idempotent: safe to run on
# every `docker compose up`. It creates a private authority, a server certificate for
# the API's internal listener, and a client certificate for the worker — then does
# nothing on subsequent runs until they expire.
#
# THIS IS A LOCAL-DEVELOPMENT CONVENIENCE. It writes private keys to a directory on
# the host so the stack comes up without a manual step. That trade is fine on a laptop
# and wrong everywhere else: a real deployment issues these from an authority it
# already runs, keeps the keys somewhere they can be rotated, and never generates a
# certificate authority as a side effect of starting a service.

set -eu

DIR=/pki
CA_DAYS=3650
LEAF_DAYS=825

CA_CN=dbr-dev-ca

# The name the API's internal listener is reached by inside the compose network, plus
# the two a developer would use from the host. A certificate is only valid for the
# names it lists, and the worker checks that — so the list has to cover every address
# the listener is actually dialled at.
SERVER_ALT="DNS:api,DNS:localhost,IP:127.0.0.1"

# What the listener checks the client's common name against. It has to match the
# InternalEdge__ClientCertificateCommonName the API is configured with, or every
# handshake is refused by a check that is working exactly as intended.
WORKER_CN=dbr-worker

present() { [ -s "$DIR/$1" ]; }

# Still valid tomorrow? `openssl x509 -checkend` exits non-zero when the certificate
# expires within the given window, which is what makes this self-healing rather than
# something that starts failing quietly a year from now.
fresh() { openssl x509 -in "$DIR/$1" -noout -checkend 86400 >/dev/null 2>&1; }

if present ca.crt && present ca.key \
  && present server.crt && present server.key \
  && present worker.crt && present worker.key \
  && fresh ca.crt && fresh server.crt && fresh worker.crt; then
  echo "pki-init: certificates already present and current"
  exit 0
fi

echo "pki-init: issuing a development authority and two certificates"

TMP=$(mktemp -d)
# shellcheck disable=SC2064
trap "rm -rf '$TMP'" EXIT

# The authority. Marked critical so a client that honours basic constraints refuses to
# accept it as anything but a signer.
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days "$CA_DAYS" \
  -keyout "$DIR/ca.key" -out "$DIR/ca.crt" \
  -subj "/CN=$CA_CN" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1

issue() {
  name=$1
  common_name=$2
  usage=$3
  alt=$4

  openssl req -newkey rsa:2048 -nodes -sha256 \
    -keyout "$DIR/$name.key" -out "$TMP/$name.csr" \
    -subj "/CN=$common_name" >/dev/null 2>&1

  {
    echo "basicConstraints=CA:FALSE"
    echo "keyUsage=critical,digitalSignature,keyEncipherment"
    echo "extendedKeyUsage=$usage"
    [ -n "$alt" ] && echo "subjectAltName=$alt"
  } > "$TMP/$name.ext"

  openssl x509 -req -in "$TMP/$name.csr" \
    -CA "$DIR/ca.crt" -CAkey "$DIR/ca.key" -CAcreateserial \
    -out "$DIR/$name.crt" -days "$LEAF_DAYS" -sha256 \
    -extfile "$TMP/$name.ext" >/dev/null 2>&1
}

# serverAuth and clientAuth rather than both on both. A certificate that can do either
# job is one that lets a compromised worker impersonate the listener it calls.
issue server "api-internal" serverAuth "$SERVER_ALT"
issue worker "$WORKER_CN" clientAuth ""

chmod 600 "$DIR"/*.key
chmod 644 "$DIR"/*.crt

echo "pki-init: ready — authority $CA_CN, listener api-internal, client $WORKER_CN"
