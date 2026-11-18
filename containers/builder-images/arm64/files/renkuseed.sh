#!/bin/sh
# SPDX-License-Identifier: MIT
# Installs the builder container's ssh key on every boot: copies authorized_keys from the read-only
# FAT seed disk labelled RENKUSEED to ~/config/settings/ssh, then unmounts it. sshd reads the file at
# each login, so running alongside sshd is fine. Started by /system/settings/launch/renkuseed; logs to the serial console.
exec > /dev/dprintf 2>&1
dir=/boot/home/config/settings/ssh
# The seed disk may be probed after this job starts, so retry for a minute.
for i in $(seq 60); do
	for name in RENKUSEED renkuseed; do
		[ -f "/$name/authorized_keys" ] || mountvolume -s -ro "$name"
		[ -f "/$name/authorized_keys" ] || continue
		mkdir -p "$dir" && chmod 700 "$dir" \
			&& cp "/$name/authorized_keys" "$dir/authorized_keys.new" \
			&& chmod 600 "$dir/authorized_keys.new" \
			&& mv -f "$dir/authorized_keys.new" "$dir/authorized_keys" \
			|| { echo "renkuseed: cannot install $dir/authorized_keys"; exit 1; }
		# The copied file's vnode can still be referenced for a moment ("busy").
		for j in $(seq 10); do unmount "/$name" 2> /dev/null && break; sleep 1; done
		[ -f "/$name/authorized_keys" ] && echo "renkuseed: warning: cannot unmount /$name"
		echo "renkuseed: installed authorized_keys from /$name"
		exit 0
	done
	sleep 1
done
echo "renkuseed: no RENKUSEED volume with authorized_keys after 60 s"
exit 1
