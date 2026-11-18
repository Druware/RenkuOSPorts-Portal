#!/bin/bash
# Usage: tests/run-haiku-tools-test.sh <dir-with-real-hpkgs>
# Runs the test that drives Haiku's package/package_repo on real packages, in a Linux container from the
# Dockerfile's tools-test stage (SDK + tools, RENKUPORTS_REQUIRE_HAIKU_TOOLS=1 so it cannot skip).
# The sources are copied inside the container so the host's bin/ and obj/ are left alone.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
hpkgs=$(cd "${1:?usage: $0 <dir-with-real-hpkgs>}" && pwd)
docker build -q --target tools-test -t renkuports-tools-test "$root" > /dev/null
docker run --rm -v "$root:/src:ro" -v "$hpkgs:/hpkg:ro" -e RENKUPORTS_TEST_HPKG_DIR=/hpkg renkuports-tools-test bash -c '
	mkdir /w && cd /src && tar --exclude=bin --exclude=obj --exclude=node_modules -cf - src tests | tar -xf - -C /w
	cd /w && dotnet test tests/RenkuPorts.Server.Tests --filter "FullyQualifiedName~RepoIndexTests" --logger "console;verbosity=normal"'
