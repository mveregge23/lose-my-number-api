#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Every hand-written source file carries a two-line SPDX tag. This is what keeps
# that true. Without it the convention holds only as long as everyone remembers,
# and a file that ships without its tag is one whose licence a downstream
# consumer has to infer from the repository root — which is exactly what SPDX
# exists to avoid.
#
# Machine-generated and vendored files are excluded, not because the licence
# doesn't apply but because we don't write them.

set -euo pipefail

cd "$(dirname "$0")/.."

missing=()

while IFS= read -r file; do
    # Only the first few lines: the tag belongs at the top, and a copy buried
    # halfway down a file isn't the convention being followed.
    if ! head -n 5 "$file" | grep -q 'SPDX-License-Identifier:'; then
        missing+=("$file")
    fi
done < <(git ls-files \
    '*.cs' '*.sql' '*.sh' '*.yml' '*.yaml' '*.csproj' '*.props' 'Dockerfile' '**/Dockerfile' \
    ':!:**/obj/**' ':!:**/bin/**')

if [ ${#missing[@]} -gt 0 ]; then
    echo "Missing an SPDX-License-Identifier tag in the first 5 lines:"
    printf '  %s\n' "${missing[@]}"
    echo
    echo "Add, with the comment marker the file's language uses:"
    echo "  SPDX-FileCopyrightText: 2026 Max Veregge"
    echo "  SPDX-License-Identifier: AGPL-3.0-or-later"
    exit 1
fi

echo "SPDX headers present in all $(git ls-files '*.cs' '*.sql' '*.sh' '*.yml' '*.yaml' '*.csproj' '*.props' 'Dockerfile' '**/Dockerfile' | wc -l | tr -d ' ') checked files."
