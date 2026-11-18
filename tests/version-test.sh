#!/usr/bin/env bash
# Checks the scripts/version.sh mapping against a throwaway git repository.
set -euo pipefail
script="$(cd "$(dirname "$0")/.." && pwd)/scripts/version.sh"
repo=$(mktemp -d); trap 'rm -rf "$repo"' EXIT
fail=0
expect() { # <pattern> <what>
  local got; got=$("$script" "$repo")
  if [[ $got =~ ^$1$ ]]; then echo "ok   $2: $got"; else echo "FAIL $2: got '$got', want /$1/"; fail=1; fi
}
commit() { echo "$1" >> "$repo/f"; git -C "$repo" add f; git -C "$repo" commit -qm "$1"; }

expect '0\.0\.0-dev' 'not a git repo'
git -C "$repo" init -q; git -C "$repo" config user.email t@t; git -C "$repo" config user.name t
commit a; commit b
expect '0\.0\.0-dev\.2\+g[0-9a-f]{7}' 'no tags'
git -C "$repo" tag -a v1.2.3 -m v1.2.3
expect '1\.2\.3' 'on the tag'
echo x >> "$repo/f"
expect '1\.2\.4-dev\.0\+g[0-9a-f]{7}\.dirty' 'tag, dirty tree'
git -C "$repo" checkout -q f; commit c; commit d
expect '1\.2\.4-dev\.2\+g[0-9a-f]{7}' 'two commits after the tag'
git -C "$repo" tag v1.3.0-rc1
expect '1\.2\.4-dev\.2\+g[0-9a-f]{7}' 'pre-release tags ignored'
git -C "$repo" tag v1.3.0
expect '1\.3\.0' 'lightweight tag'
exit $fail
