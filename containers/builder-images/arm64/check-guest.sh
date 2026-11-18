# SPDX-License-Identifier: MIT
# Runs in the guest during the provisioning boot (inside-builder.sh provision): fails unless the
# tools the builder entrypoint and HaikuPorter use are there.
set -e
uname -a
echo "date: $(date -u)"
for c in haikuporter pkgman tar shutdown stat date curl wget gcc make python3 git sshd mountvolume; do
	p=$(command -v "$c") || { echo "missing: $c"; exit 1; }
	echo "$c: $p"
done
stat -c '%Y %n' /boot/home
haikuporter --help | head -1
ls -l /boot/home/config/settings/ssh /boot/system/settings/ssh
pkgman list-repos
df /boot || true
ls /boot/system/packages | grep -E '^haiku(_devel)?[-.]|^makefile_engine|^haikuporter' || true
