# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# What the API is allowed to ask the key manager for — and nothing else.
#
# This file is the enforceable version of "least privilege". Until it existed the
# services authenticated as root, which meant the sentence "workers never hold
# standing decryption rights" was a description of intent rather than of the system.
#
# Everything not granted here is denied: policies are deny-by-default, so the absence
# of a path is as load-bearing as its presence. Two absences are worth naming, because
# they are deliberate rather than overlooked:
#
#   * No `list` on transit/keys. The keys are named after tenants, so listing them
#     would enumerate every account on the instance — the exact question the rest of
#     this design refuses to answer. A token that can decrypt is one thing; a token
#     that can tell you who exists is another, and this one cannot.
#
#   * No `read` on a key. Transit never exports key material anyway, but reading a
#     key returns its metadata and version history, which is more than encrypting
#     and decrypting requires.
#
# The paths are scoped to the `tenant-` prefix the provider generates. That is not
# decoration: a bug that named a key anything else would be refused here rather than
# quietly getting the run of the mount.

# Creating a tenant's wrapping key, and setting deletion_allowed on it so the account
# can genuinely be erased later. `create` and `update` because a first write and a
# subsequent one are different capabilities, and this runs on a path that may repeat.
#
# `delete` is the irreversible one: it destroys the wrapping key and with it every
# data key that key ever wrapped, wherever those live. It is granted because account
# deletion has to be real — and it is precisely why this token should not also be able
# to enumerate what it could destroy.
path "transit/keys/tenant-*" {
  capabilities = ["create", "update", "delete"]
}

# Minting a data key: returns the key to encrypt with now and the wrapped form to
# store. `update` is the capability a POST to an existing path needs.
path "transit/datakey/plaintext/tenant-*" {
  capabilities = ["update"]
}

# Turning a stored wrapped key back into one that can decrypt.
path "transit/decrypt/tenant-*" {
  capabilities = ["update"]
}
