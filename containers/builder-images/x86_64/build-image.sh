#!/bin/bash
# Builds the x86_64 RenkuPorts builder image from RenkuOS source. Steps and requirements: README.md.
# Usage: RENKUOS_SRC=<RenkuOS/Source checkout> build-image.sh [env|jam|provision|pack]...   (no step: all four)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${OUT:-$here/../out}/x86_64
mkdir -p "$out/logs"
out=$(cd "$out" && pwd)
ENV_IMAGE=renkuports-builder-image-env:x86_64
BUILDER=${BUILDER_IMAGE:-renkuports-builder:dev}
VOLUME=${WORK_VOLUME:-renkuports-builder-image-x86_64}

step_env() { docker build -t "$ENV_IMAGE" "$here"; }
step_jam() {
	: "${RENKUOS_SRC:?set RENKUOS_SRC to a RenkuOS/Source checkout}"
	docker run --rm -v "$RENKUOS_SRC:/src-ro:ro" -v "$here:/tooling:ro" -v "$VOLUME:/w" -v "$out:/out" \
		-e REF="${RENKUOS_REF:-refs/remotes/origin/main}" -e HOME=/tmp "$ENV_IMAGE" /tooling/jam-build.sh
}
# Provisioning and packing run in the builder container itself, so the boot uses its exact QEMU argv.
in_builder() {
	docker run --rm --init --name "renkuports-x86_64-image-${1%.sh}" --entrypoint "/tooling/$1" -e RENKUPORTS_GUEST_MB="${RENKUPORTS_GUEST_MB:-4096}" \
		-v "$here:/tooling:ro" -v "$out:/cache/work" -v "$out/logs:/cache/logs" "$BUILDER"
}
step_provision() { in_builder provision.sh; }
step_pack() { in_builder pack.sh; }

[ $# -gt 0 ] || set -- env jam provision pack
for s in "$@"; do
	case $s in env|jam|provision|pack) echo "== $s $(date -u +%FT%TZ)"; "step_$s" ;; *) echo "unknown step $s" >&2; exit 2 ;; esac
done
echo "== done $(date -u +%FT%TZ)"
