#!/bin/bash
# SPDX-License-Identifier: MIT
# Builds the RenkuPorts arm64 builder image (containers/builder, "Builder image requirements") from a
# RenkuOS source checkout: jam image with HaikuPorter and build tools, MBR+ESP+BFS disk, one
# provisioning boot under the builder container's own QEMU argv, then a .qcow2.xz in out/.
#
#   build.sh [phase...]     phases: setup jam assemble provision package (default: all, in order)
#
# Environment (defaults in brackets):
#   SRC        RenkuOS git checkout, mounted read-only (required)
#   REF        ref of SRC to build [refs/remotes/origin/main]
#   VOL        Docker volume holding the build tree and scratch disks [renkuports-arm64-image]
#   SEED_VOL   volume with an already built /src/generated.arm64 to start from, copied once into VOL
#              (saves hours; empty: clone SRC and configure from scratch) [renkuos-arm64-build]
#   TOOLCHAIN  image with jam and /tools/cross-tools-arm64 [renkuos-cross-compiler:arm64-master-native]
#   BUILDER    renkuports-builder image for this host (QEMU, AAVMF) [renkuports-builder:dev-arm64]
#   JOBS, GUEST_MB  jam -j and the provisioning guest's CPUs and memory [4, 4096]
#   REPO_URL   the only pkgman repository the image keeps [https://renkuos.freemyip.com/repo/renkuosports/arm64]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
SRC=${SRC:?set SRC to a RenkuOS git checkout}
REF=${REF:-refs/remotes/origin/main}
VOL=${VOL:-renkuports-arm64-image}
SEED_VOL=${SEED_VOL-renkuos-arm64-build}
TOOLCHAIN=${TOOLCHAIN:-renkuos-cross-compiler:arm64-master-native}
BUILDER=${BUILDER:-renkuports-builder:dev-arm64}
JOBS=${JOBS:-4}
GUEST_MB=${GUEST_MB:-4096}
REPO_URL=${REPO_URL:-https://renkuos.freemyip.com/repo/renkuosports/arm64}
mkdir -p "$here/out"

toolchain() { # toolchain <phase> [args]: runs inside-toolchain.sh in the cross-compiler image
	local seed=()
	[ -n "$SEED_VOL" ] && seed=(-v "$SEED_VOL:/seed:ro")
	docker run --rm --platform linux/arm64 -v "$VOL:/w" -v "$SRC:/src:ro" "${seed[@]}" -v "$here:/h:ro" \
		-e JOBS="$JOBS" "$TOOLCHAIN" bash /h/inside-toolchain.sh "$@"
}
builder() { # builder <phase>: runs inside-builder.sh in the builder image, with VOL as its /cache
	docker run --rm --init --name "renkuports-arm64-image-$1" --platform linux/arm64 -v "$VOL:/cache" -v "$here:/h:ro" -v "$here/out:/out" \
		-e REPO_URL="$REPO_URL" -e RENKUPORTS_JOBS="$JOBS" -e RENKUPORTS_GUEST_MB="$GUEST_MB" --entrypoint bash "$BUILDER" /h/inside-builder.sh "$@"
}

phases=("$@")
[ $# -gt 0 ] || phases=(setup jam assemble provision package)
for p in "${phases[@]}"; do
	echo "=== $p ($(date -u +%FT%TZ))"
	case $p in
		setup) toolchain setup "$REF" && builder hostkeys ;;
		jam|assemble) toolchain "$p" ;;
		provision|package) builder "$p" ;;
		*) echo "unknown phase $p" >&2; exit 2 ;;
	esac
done
