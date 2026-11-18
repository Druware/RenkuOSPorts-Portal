#!/usr/bin/env bash
# Prints the SemVer version of the checkout from its newest vMAJOR.MINOR.PATCH tag (git describe).
#   on the tag, clean tree          -> 1.2.3
#   5 commits after v1.2.3          -> 1.2.4-dev.5+gabc1234   (next patch, so it sorts after 1.2.3 and before 1.2.4)
#   uncommitted changes             -> 1.2.4-dev.0+gabc1234.dirty
#   no version tag yet (12 commits) -> 0.0.0-dev.12+gabc1234
#   not a git checkout              -> 0.0.0-dev
# Usage: scripts/version.sh [dir]   (default: the current directory)
set -euo pipefail
cd "${1:-.}"
git rev-parse --git-dir > /dev/null 2>&1 || { echo 0.0.0-dev; exit 0; }
sha=$(git rev-parse --short=7 HEAD)
dirty=$(git diff --quiet HEAD -- 2>/dev/null || echo .dirty)
if d=$(git describe --tags --long --match 'v[0-9]*.[0-9]*.[0-9]*' --exclude '*-*' 2>/dev/null) \
   && [[ $d =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)-([0-9]+)-g[0-9a-f]+$ ]]; then
  major=${BASH_REMATCH[1]} minor=${BASH_REMATCH[2]} patch=${BASH_REMATCH[3]} n=${BASH_REMATCH[4]}
  if [[ $n == 0 && -z $dirty ]]; then echo "$major.$minor.$patch"
  else echo "$major.$minor.$((patch + 1))-dev.$n+g$sha$dirty"; fi
else
  echo "0.0.0-dev.$(git rev-list --count HEAD)+g$sha$dirty"
fi
