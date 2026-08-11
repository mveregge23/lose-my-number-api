# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Local/self-host OpenBao config. Replaces `server -dev`, which keeps the barrel
# in memory and therefore destroys every tenant DEK on restart — leaving the
# ciphertext in Postgres permanently undecryptable.

ui = true

# /openbao/file is the path the image itself declares as a volume and pre-creates
# owned by the non-root `openbao` user. Mounting the data volume anywhere else
# yields a root-owned directory the server can't write to.
storage "file" {
  path = "/openbao/file"
}

listener "tcp" {
  address = "0.0.0.0:8200"

  # No TLS on the listener: this port is reachable only from the compose network
  # (and, with the dev-ports overlay, from localhost). A deployment exposing
  # OpenBao beyond the host terminates TLS here instead.
  tls_disable = true
}

# No `disable_mlock` setting here on purpose: OpenBao 2.x removed mlock support
# outright and refuses to start if the key is present at all — a real behavioural
# divergence from HashiCorp Vault, where mlock is still the recommended default.
# Its guidance is to protect the master key by disabling or encrypting swap at
# the host level instead. macOS encrypts swap by default; a Linux self-host
# should confirm the same before treating this as production-ready.
# https://openbao.org/docs/install/#post-installation-hardening
