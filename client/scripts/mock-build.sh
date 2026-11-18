#!/bin/bash
# Fake builder for testing renkuports-client: writes <port>-<version>-1-<arch>.hpkg into <outdir>.
# Usage: mock-build.sh <port> <version> <outdir>
# Env: ARCH (default: RENKUPORTS_PLATFORM from the client, else arm64); MOCK_FAIL=<port> also fails that port.
# Fails (exit 1, no hpkg) when the port name starts with "fail" or equals MOCK_FAIL.
# The content is deterministic, so rebuilding the same port uploads an identical file.
set -euo pipefail
[ $# -eq 3 ] || { echo "usage: $0 <port> <version> <outdir>" >&2; exit 2; }
port=$1 version=$2 out=$3
arch=${ARCH:-${RENKUPORTS_PLATFORM:-arm64}}
echo "mock build of $port $version for $arch"
if [[ $port == fail* || $port == "${MOCK_FAIL:-}" ]]; then
	echo "error: mock failure for $port" >&2
	exit 1
fi
mkdir -p "$out"
printf 'mock hpkg\nport=%s\nversion=%s\narch=%s\n' "$port" "$version" "$arch" > "$out/$port-$version-1-$arch.hpkg"
echo "wrote $out/$port-$version-1-$arch.hpkg"
