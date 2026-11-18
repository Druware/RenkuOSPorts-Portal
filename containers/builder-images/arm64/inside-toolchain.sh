#!/bin/bash
# SPDX-License-Identifier: MIT
# The build.sh phases that run in the cross-compiler image: /w is the build volume, /src the
# read-only RenkuOS checkout, /seed an optional volume with a built tree, /h this directory.
set -euo pipefail
git config --global --add safe.directory '*'
G=/w/src/generated.arm64
BEGIN_KIB=4096 ESP_KIB=32768	# the arm64 mmc layout (HAIKU_BOOT_SDIMAGE_BEGIN, BuildEfiSDImage1)

setup() {
	local ref=$1
	if [ ! -d /w/src ]; then
		if [ -d /seed/src/generated.arm64 ]; then
			echo "copying /seed/src (cp -a keeps mtimes, so jam rebuilds only what the checkout changes)"
			cp -a /seed/src /w/src
		else
			# shortcut: this path is untested here (the seed volume was used); configure as the toolchain image's build-toolchain.sh.
			git clone -q /src /w/src
			mkdir -p "$G" && (cd "$G" && ../configure --cross-tools-prefix /tools/cross-tools-arm64/bin/aarch64-unknown-haiku-)
		fi
	fi
	cd /w/src
	git fetch -q --tags /src "+$ref:refs/renkuports/build"
	git checkout -q -f --detach refs/renkuports/build
	git clean -q -fd	# untracked leftovers of the seed; ignored files (generated.*) stay
	git rev-parse HEAD > /w/renkuos.commit
	git describe --tags --match 'hrev*' HEAD > /w/renkuos.describe
	# A stale revision cache or the seed's own UserBuildConfig would override ours.
	rm -f build/jam/UserBuildConfig "$G/build/haiku-revision"
	cp /h/UserBuildConfig "$G/UserBuildConfig"
	rm -rf /w/files && mkdir -p /w/files && cp /h/files/renkuseed.sh /h/files/renkuseed.launch /w/files/ && : > /w/files/empty
	echo "RenkuOS $(cat /w/renkuos.commit) ($(cat /w/renkuos.describe))"
}

jam_build() {
	cd "$G"
	rm -f haiku-nightly.image
	jam -q -j"$JOBS" @nightly-raw
	jam -q -j"$JOBS" haiku_loader.efi
	ls -ls haiku-nightly.image
	echo "haiku revision $(cat build/haiku-revision)"
}

# MBR, ESP with the EFI loader and the jam-built BFS volume, written sparse (BuildEfiSDImage1
# concatenates with cat, which would write all 40 GiB).
assemble() {
	cd "$G"
	local img=haiku-nightly.image disk=/w/disk.raw boot=$((BEGIN_KIB + ESP_KIB)) size loader mbrtool fatshell
	size=$(stat -c %s "$img")
	loader=objects/haiku/arm64/release/system/boot/efi/haiku_loader.efi	# not a packaging copy: those carry attributes FAT cannot take
	mbrtool=$(find objects/linux -name mbrtool -type f -perm -u+x | head -1)
	fatshell=$(find objects/linux -name fat_shell -type f -perm -u+x | head -1)
	[ -s "$loader" ] && [ -n "$mbrtool" ] && [ -n "$fatshell" ] || { echo "missing loader or host tools" >&2; exit 1; }
	export LD_LIBRARY_PATH=$G/objects/linux/lib
	rm -f "$disk"
	truncate -s $((boot * 1024 + size)) "$disk"
	"$mbrtool" "$disk" 0 0xef "$BEGIN_KIB" "$ESP_KIB"
	"$mbrtool" "$disk" 1 0xeb "$boot" $((size / 1024))
	local fat=("$fatshell" --start-offset $((BEGIN_KIB * 1024)))
	"${fat[@]}" --end-offset $((boot * 1024)) --initialize "$disk" 'Haiku Boot'
	echo "mkdir -p myfs/EFI/BOOT" | "${fat[@]}" "$disk"
	echo "cp :$loader myfs/EFI/BOOT/BOOTAA64.EFI" | "${fat[@]}" "$disk"
	echo "ls myfs/EFI/BOOT" | "${fat[@]}" "$disk" | tee /dev/stderr \
		| grep -q " $(stat -c %s "$loader") .*BOOTAA64.EFI" || { echo "BOOTAA64.EFI did not copy" >&2; exit 1; }
	dd if="$img" of="$disk" bs=1M seek=$((boot / 1024)) conv=sparse,notrunc status=none
	cat build/haiku-revision > /w/haiku.revision
	ls -ls "$disk"
}

case ${1:-} in
	setup) setup "$2" ;;
	jam) jam_build ;;
	assemble) assemble ;;
	*) echo "usage: inside-toolchain.sh setup <ref> | jam | assemble" >&2; exit 2 ;;
esac
