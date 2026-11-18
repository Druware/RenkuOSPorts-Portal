#!/bin/bash
# Runs in the renkuports-builder container (see build-image.sh): compacts the provisioned /cache/work/x86_64.qcow2,
# compresses it to the release asset and writes its images.json entry (url left empty until it is published).
set -euo pipefail
cd /cache/work
. ./build-info
name=renkuports-guest-x86_64-${rev//+/_}-${commit:0:12}
qemu-img convert -O qcow2 x86_64.qcow2 "$name.qcow2"
xz -T4 -9 -c "$name.qcow2" > "$name.qcow2.xz.tmp" && mv "$name.qcow2.xz.tmp" "$name.qcow2.xz"
jq -n --arg sha "$(sha256sum < "$name.qcow2.xz" | cut -d' ' -f1)" --arg disk "$(sha256sum < "$name.qcow2" | cut -d' ' -f1)" \
	--argjson bytes "$(stat -c %s "$name.qcow2.xz")" --arg rev "$rev" \
	'{url: "", sha256: $sha, diskSha256: $disk, bytes: $bytes, haikuRev: $rev}' > images-entry.json
# A file:// manifest for testing the image with the builder container (out/x86_64 mounted at /images).
jq --arg url "file:///images/$name.qcow2.xz" '{version: 1, images: {x86_64: (. + {url: $url})}}' images-entry.json > images-local.json
ls -l "$name.qcow2" "$name.qcow2.xz"
cat images-entry.json
