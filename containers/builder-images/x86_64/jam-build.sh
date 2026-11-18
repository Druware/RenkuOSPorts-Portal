#!/bin/bash
# Runs in the build-env container (see build-image.sh): checks out $REF of the RenkuOS tree mounted at /src-ro into
# /w/src, builds @nightly-raw with ./UserBuildConfig and writes /out/disk-raw.qcow2 and /out/build-info.
set -euo pipefail
# The checkout's origin/main: its local main can lag far behind.
REF=${REF:-refs/remotes/origin/main}

git config --global --add safe.directory '*'
[ -d /w/src/.git ] || git init -q /w/src
cd /w/src
git fetch -q --force --tags /src-ro "$REF"
git checkout -q --force --detach FETCH_HEAD
git clean -qfdx -e /generated.x86_64
commit=$(git rev-parse HEAD)
# As .github/workflows/image.yml: an hrev-shaped revision, since HaikuPorts packages require haiku >= an hrev version.
rev=$(git describe --tags --match='hrev*' --abbrev=1 | sed 's/-g[0-9a-z]\+//; s/-/+/g')
echo "RenkuOS $REF = $commit ($rev)"

export HAIKU_REVISION=$rev HAIKU_IMAGE_LABEL=RenkuOS
printf 'tries = 10\nwaitretry = 5\ntimeout = 60\nretry_connrefused = on\n' > "$HOME/.wgetrc"
mkdir -p generated.x86_64 && cd generated.x86_64
cp /tooling/UserBuildConfig .
../configure --cross-tools-prefix /tools/cross-tools-x86_64/bin/x86_64-unknown-haiku- --distro-compatibility default
# The revision is cached here (UserBuildConfig.ReadMe); the image is recreated, never updated in place.
rm -f build/haiku-revision haiku-nightly.image
# One retry: a fresh tree once failed with btrfs compiled before build_packages/zlib's headers were extracted.
jam -q -j"$(nproc)" @nightly-raw || { echo "jam failed, retrying once"; jam -q -j"$(nproc)" @nightly-raw; }

qemu-img convert -O qcow2 haiku-nightly.image /out/disk-raw.qcow2
printf 'commit=%s\nrev=%s\nref=%s\n' "$commit" "$rev" "$REF" > /out/build-info
ls -ls haiku-nightly.image /out/disk-raw.qcow2
