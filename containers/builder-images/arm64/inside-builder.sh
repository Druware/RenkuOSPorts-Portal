#!/bin/bash
# SPDX-License-Identifier: MIT
# The build.sh phases that run in the renkuports-builder image: /cache is the build volume, /out
# this directory's out/, /h this directory. The provisioning boot uses the entrypoint's own QEMU argv.
set -euo pipefail
W=/cache
REPO_URL=${REPO_URL:-https://renkuos.freemyip.com/repo/renkuosports/arm64}

hostkeys() {
	rm -rf "$W/files/ssh" && mkdir -p "$W/files/ssh"
	for t in ed25519 ecdsa rsa; do
		ssh-keygen -q -t "$t" -N '' -C renkuports-builder -f "$W/files/ssh/ssh_host_${t}_key"
	done
	chmod 600 "$W"/files/ssh/*_key
}

# Boots the image once as the container would (seed disk with a throwaway key), checks the tools a
# build uses, lets the first-boot work finish, removes the throwaway key and powers off.
provision() {
	local key=/tmp/provision_key pid
	mkdir -p "$W/work" "$W/logs"
	rm -f "$W/work/arm64.qcow2"
	qemu-img convert -f raw -O qcow2 "$W/disk.raw" "$W/work/arm64.qcow2"
	cp /usr/share/AAVMF/AAVMF_VARS.fd "$W/work/arm64-vars.fd"
	ssh-keygen -q -t ed25519 -N '' -C provision -f "$key"
	rm -f /run/seed.img
	mkfs.vfat -C -n RENKUSEED /run/seed.img 1440 > /dev/null && mcopy -i /run/seed.img "$key.pub" ::authorized_keys
	local qemu; mapfile -t qemu < <(renkuports-build qemu-argv arm64 tcg)
	echo "booting: ${qemu[*]}"
	"${qemu[@]}" & pid=$!
	local ssh=(ssh -i "$key" -p 2222 -o BatchMode=yes -o ConnectTimeout=5 -o LogLevel=ERROR
		-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null user@127.0.0.1)
	local start=$SECONDS
	until "${ssh[@]}" true 2> /dev/null; do
		kill -0 "$pid" 2> /dev/null || { tail -50 "$W/logs/arm64-serial.log"; echo "QEMU exited during boot" >&2; exit 1; }
		[ $((SECONDS - start)) -lt 3600 ] || { kill "$pid"; tail -50 "$W/logs/arm64-serial.log"; echo "no ssh after 3600 s" >&2; exit 1; }
		sleep 5
	done
	echo "ssh up after $((SECONDS - start)) s"
	"${ssh[@]}" "date -s @$(date +%s)" > /dev/null
	"${ssh[@]}" sh -s < /h/check-guest.sh
	# package_daemon deletes this marker once the first boot's post-install scripts have run; the
	# published base should start from a finished first boot.
	"${ssh[@]}" 'f=/boot/system/packages/administrative/FirstBootProcessingNeeded
		for i in $(seq 120); do [ -e $f ] || exit 0; sleep 5; done; echo "first boot processing not finished"; exit 1'
	# The image's own repository configs point at a HaikuPorts arm64 repository that does not exist
	# and at the RenkuOS system repository (which could replace the built system packages); the
	# RenkuOSPorts arm64 repository is what --get-dependencies installs from. (drop-repo takes
	# --yes but not -y: "y" is missing from its getopt string.)
	"${ssh[@]}" "pkgman drop-repo --yes HaikuPorts && pkgman drop-repo --yes Renku && pkgman add-repo '$REPO_URL' && pkgman list-repos"
	"${ssh[@]}" 'rm -f ~/config/settings/ssh/authorized_keys; sync; shutdown' || true
	start=$SECONDS
	while kill -0 "$pid" 2> /dev/null; do
		[ $((SECONDS - start)) -lt 300 ] || { kill "$pid"; echo "guest did not power off" >&2; exit 1; }
		sleep 2
	done
	wait "$pid" || true
	echo "guest powered off after $((SECONDS - start)) s"
	cp "$W/logs/arm64-serial.log" /out/provision-serial.log
}

# A compacted copy of the provisioned disk, xz-compressed, and its manifest entry (url left empty).
package() {
	local name sha disksha bytes rev
	name=renkuports-guest-arm64-$(cat "$W/renkuos.describe").qcow2
	rev=$(cat "$W/haiku.revision")
	rm -f "$W/$name" "/out/$name.xz"
	qemu-img convert -O qcow2 "$W/work/arm64.qcow2" "$W/$name"
	qemu-img check -q "$W/$name"
	xz -T"${RENKUPORTS_JOBS:-4}" -6 -c "$W/$name" > "/out/$name.xz.part" && mv "/out/$name.xz.part" "/out/$name.xz"
	sha=$(sha256sum < "/out/$name.xz" | cut -d' ' -f1)
	disksha=$(sha256sum < "$W/$name" | cut -d' ' -f1)
	bytes=$(stat -c %s "/out/$name.xz")
	echo "$sha  $name.xz" > "/out/$name.xz.sha256"
	jq -n --arg sha "$sha" --arg d "$disksha" --argjson b "$bytes" --arg r "$rev" --arg c "$(cat "$W/renkuos.commit")" \
		'{arm64: {url: "", sha256: $sha, diskSha256: $d, bytes: $b, haikuRev: $r}, renkuosCommit: $c}' > /out/images-entry.json
	qemu-img info "$W/$name"
	ls -l "/out/$name.xz"
	cat /out/images-entry.json
	[ "$bytes" -lt 2147483648 ] || echo "warning: $name.xz is over GitHub's 2 GiB release asset limit" >&2
}

case ${1:-} in
	hostkeys) hostkeys ;;
	provision) provision ;;
	package) package ;;
	*) echo "usage: inside-builder.sh hostkeys | provision | package" >&2; exit 2 ;;
esac
