#!/bin/bash
# Usage: tests/run-haiku-tools-test.sh <dir-with-real-hpkgs> [dotnet test args...]
# Runs the whole test suite in a Linux container from the Dockerfile's tools-test stage (SDK + Haiku's package and
# package_repo tools, RENKUPORTS_REQUIRE_HAIKU_TOOLS=1 so the tool tests cannot skip), on the compose network so it
# reaches the sql and azurite services (start them first: docker compose up -d sql azurite).
# The sources are copied inside the container so the host's bin/ and obj/ are left alone.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
hpkgs=$(cd "${1:?usage: $0 <dir-with-real-hpkgs> [dotnet test args...]}" && pwd)
shift
sql=$(cd "$root" && docker compose ps -q sql)
[ -n "$sql" ] || { echo "start the compose sql and azurite services first"; exit 1; }
network=$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}}{{end}}' "$sql")
docker build -q --target tools-test -t renkuports-tools-test "$root" > /dev/null
docker run --rm --network "$network" -v "$root:/src:ro" -v "$hpkgs:/hpkg:ro" -e RENKUPORTS_TEST_HPKG_DIR=/hpkg \
	-e RENKUPORTS_TEST_SQL_SERVER=sql,1433 \
	-e "RENKUPORTS_TEST_BLOB=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;" \
	renkuports-tools-test bash -c '
	mkdir /w && cd /src && tar --exclude=bin --exclude=obj --exclude=node_modules -cf - src tests | tar -xf - -C /w
	cd /w && dotnet test tests/RenkuPorts.Server.Tests --logger "console;verbosity=normal" "$@"' -- "$@"
