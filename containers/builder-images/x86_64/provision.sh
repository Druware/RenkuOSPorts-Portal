#!/bin/bash
# Runs in the renkuports-builder container (see build-image.sh). Boots a copy of /cache/work/disk-raw.qcow2 with the
# container's own x86_64 TCG argv and a throwaway seed key until ssh works: the first boot runs the packages'
# post-install scripts, among them openssh's host key generation, after net_server has already given up on sshd,
# so a second boot is normal. Then it installs $PKGMAN_INSTALL from HaikuPorts, checks the commands the entrypoint
# uses and powers the guest off.
# Leaves /cache/work/x86_64.qcow2 (the provisioned disk).
set -uo pipefail
BOOT_WAIT=${BOOT_WAIT:-600}
# Build tools jam cannot put into the image (a dependency is missing from its pinned package list).
PKGMAN_INSTALL=${PKGMAN_INSTALL:-libtool}
key=/run/provision-key qmp_sock=/run/qmp.sock serial=/cache/logs/x86_64-serial.log

die() { echo "error: $*" >&2; exit 1; }
cp /cache/work/disk-raw.qcow2 /cache/work/x86_64.qcow2 || die "cannot copy the raw disk"
ssh-keygen -q -t ed25519 -N '' -C provision -f "$key" || die "ssh-keygen failed"
mkfs.vfat -C -n RENKUSEED /run/seed.img 1440 > /dev/null && mcopy -i /run/seed.img "$key.pub" ::authorized_keys \
	|| die "cannot make the seed disk"
mapfile -t qemu < <(renkuports-build qemu-argv x86_64 tcg)
[ ${#qemu[@]} -gt 0 ] || die "no QEMU argv"
SSH=(ssh -i "$key" -p 2222 -o BatchMode=yes -o ConnectTimeout=5 -o LogLevel=ERROR
	-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null user@127.0.0.1)
qmp() { printf '{"execute":"qmp_capabilities"}\n{"execute":"%s"}\n' "$1" | timeout 5 socat - "UNIX-CONNECT:$qmp_sock" > /dev/null 2>&1; }
# wait_exit <seconds>: true once QEMU has exited.
wait_exit() { local t=$((SECONDS + $1)); while kill -0 "$pid" 2> /dev/null; do [ $SECONDS -lt $t ] || return 1; sleep 2; done; wait "$pid"; }

up=0
for boot in 1 2 3; do
	echo "boot $boot: ${qemu[*]}"
	"${qemu[@]}" & pid=$!
	start=$SECONDS
	while kill -0 "$pid" 2> /dev/null && [ $((SECONDS - start)) -lt "$BOOT_WAIT" ]; do
		"${SSH[@]}" true 2> /dev/null && { up=1; break; }
		sleep 10
	done
	[ $up = 1 ] && { echo "boot $boot: ssh up after $((SECONDS - start))s"; break; }
	kill -0 "$pid" 2> /dev/null || die "QEMU exited during boot $boot"
	echo "boot $boot: no ssh after ${BOOT_WAIT}s, ACPI power-off and boot again"
	qmp system_powerdown
	wait_exit 300 || { echo "boot $boot: no power-off, QMP quit"; qmp quit; wait_exit 30 || kill -9 "$pid"; }
done
[ $up = 1 ] || die "no ssh after 3 boots; see $serial"

# Only HaikuPorts stays: the image's Renku repository could replace the system packages built here, and the
# third-party ones (Spritz Depot, from Source/data/settings/package-repositories) are not wanted. This pkgman's
# "drop-repo -y" is broken; "--yes" works. Repository names are the lines indented by exactly 8 spaces.
echo "dropping every pkgman repository but HaikuPorts"
"${SSH[@]}" '
	for name in $(pkgman list-repos | sed -n "s/^        \([^ 	].*\)$/\1/p" | grep -vx HaikuPorts); do
		pkgman drop-repo --yes "$name" || exit 1
	done
	pkgman list-repos
	[ "$(pkgman list-repos | grep -c base-url:)" = 1 ] \
		&& pkgman list-repos | grep -q "base-url:  https://eu.hpkg.haiku-os.org/haikuports/master/x86_64/current"
' || { qmp quit; die "cannot reduce the repositories to HaikuPorts"; }

if [ -n "$PKGMAN_INSTALL" ]; then
	echo "pkgman install $PKGMAN_INSTALL"
	"${SSH[@]}" "pkgman refresh && pkgman install -y $PKGMAN_INSTALL" || { qmp quit; die "pkgman install failed"; }
fi

"${SSH[@]}" '
	missing=
	for c in haikuporter python3 git patch gcc make libtoolize tar xz pkgman shutdown stat date mountvolume; do
		command -v $c > /dev/null || missing="$missing $c"
	done
	[ -z "$missing" ] || { echo "missing:$missing"; exit 1; }
	uname -a; haikuporter --version; python3 --version; git --version; gcc --version | head -1
	stat -c "%Y %n" /boot/system/packages/haiku_devel*.hpkg; date -s @'"$(date +%s)"' > /dev/null && date -u
	pkgman list-repos
	cat /var/log/renkuports-seed.log
	rm -f ~/config/settings/ssh/authorized_keys /var/log/renkuports-seed.log
	df -h /boot | tail -1
' || { qmp quit; die "guest check failed"; }

echo "powering off"
"${SSH[@]}" shutdown > /dev/null 2>&1
# Seen once right after the pkgman transaction: shutdown returned but the guest stayed up. ACPI power-off then works.
wait_exit 120 || { echo "no power-off after ssh shutdown, ACPI power-off"; qmp system_powerdown; wait_exit 300; } \
	|| { qmp quit; die "guest did not power off"; }
echo "provisioned: /cache/work/x86_64.qcow2"
