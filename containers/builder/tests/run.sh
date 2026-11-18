#!/bin/bash
# Container tests, run on the host against a built image (no guest is booted).
# Usage: containers/builder/tests/run.sh [image]   (default renkuports-builder:dev)
set -uo pipefail
IMG=${1:-renkuports-builder:dev}
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d) vol=renkuports-test-$$ vol2=renkuports-test2-$$ job=renkuports-test-job-$$
mkdir -p "$tmp/out" "$tmp/img"
trap 'docker rm -f $job > /dev/null 2>&1; docker volume rm -f $vol $vol2 > /dev/null; rm -rf "$tmp"' EXIT
fails=0
arch=$(docker image inspect -f '{{.Architecture}}' "$IMG") || exit 1
# An explicit platform keeps docker's mismatch warning out of the logs when testing an emulated image.
docker() { if [ "$1" = run ]; then shift; command docker run --platform "linux/$arch" "$@"; else command docker "$@"; fi; }
pass() { echo "ok   $1"; }
fail() { echo "FAIL $1"; fails=$((fails + 1)); [ -f "$tmp/log" ] && sed 's/^/     /' "$tmp/log"; }
dr() { docker run --rm --init -v "$vol:/cache" -v "$tmp/out:/out" -v "$tmp/img:/t" "$@"; }
# expect <exit code> <name> <docker run args...>
expect() {
	local want=$1 name=$2; shift 2
	dr "$@" > "$tmp/log" 2>&1
	local got=$?
	[ "$got" = "$want" ] && pass "$name" || fail "$name (exit $got, want $want)"
}
clean_out() { rm -f "$tmp/out"/*; }

echo "# argument validation"
expect 2 "no subcommand" "$IMG"
expect 2 "unknown subcommand" "$IMG" bogus
expect 2 "mock without arguments" "$IMG" mock
expect 2 "mock with two arguments" "$IMG" mock arm64 foo
expect 2 "arch with capitals" "$IMG" mock ARM64 foo 1.0
expect 2 "unknown arch" "$IMG" build mips foo 1.0
expect 2 "port with a slash" "$IMG" mock arm64 foo/bar 1.0
expect 2 "version with a space" "$IMG" build arm64 foo "1 0"
expect 2 "bad RENKUPORTS_JOBS" -e RENKUPORTS_JOBS=x "$IMG" mock arm64 foo 1.0
expect 2 "bad RENKUPORTS_ACCEL" -e RENKUPORTS_ACCEL=fast "$IMG" mock arm64 foo 1.0
expect 2 "info with a bad flag" "$IMG" info --yaml
expect 2 "RENKUPORTS_REPO_URL with a quote" -e "RENKUPORTS_REPO_URL=https://repo.example/x'; rm -rf /" "$IMG" build arm64 foo 1.0
expect 2 "RENKUPORTS_REPO_URL not http(s)" -e RENKUPORTS_REPO_URL=ftp://repo.example/x "$IMG" mock arm64 foo 1.0
expect 3 "riscv64 is unsupported" "$IMG" build riscv64 foo 1.0
grep -q "riscv64 is not supported" "$tmp/log" && pass "riscv64 message" || fail "riscv64 message"
expect 3 "RENKUPORTS_ACCEL=kvm without /dev/kvm" -e RENKUPORTS_ACCEL=kvm "$IMG" mock arm64 foo 1.0

echo "# info --json"
expect 0 "info --json exits 0" "$IMG" info --json
jq -e --arg a "${arch/amd64/x86_64}" '(keys == ["accel","guestImages","imageArch","targets","unsupported","version"])
	and .imageArch == $a and .targets == ["arm64","x86_64","x86"] and (.unsupported | has("riscv64"))
	and (.accel | keys_unsorted == ["arm64","x86_64","x86"] and all(.[]; . == "kvm" or . == "tcg"))
	and (.guestImages == {"arm64":"missing","x86_64":"missing","x86":"missing"}) and (.version | length > 0)' "$tmp/log" > /dev/null \
	&& pass "info --json shape" || fail "info --json shape"

echo "# mock"
clean_out
expect 0 "mock success" -e MOCK_TIMESTAMP= "$IMG" mock arm64 libfoo 1.2.3
mkdir -p "$tmp/ref" && ARCH=arm64 "$here/../../../client/scripts/mock-build.sh" libfoo 1.2.3 "$tmp/ref" > /dev/null
cmp -s "$tmp/out/libfoo-1.2.3-1-arm64.hpkg" "$tmp/ref/libfoo-1.2.3-1-arm64.hpkg" && pass "mock hpkg matches mock-build.sh" || fail "mock hpkg matches mock-build.sh"
[ "$(ls "$tmp/out")" = libfoo-1.2.3-1-arm64.hpkg ] && pass "only the hpkg in /out" || fail "only the hpkg in /out"
grep -q '^\[host\] mock build of libfoo' "$tmp/log" && pass "[host] log prefix" || fail "[host] log prefix"
cp "$tmp/out/libfoo-1.2.3-1-arm64.hpkg" "$tmp/first.hpkg"
expect 0 "mock rebuild" "$IMG" mock arm64 libfoo 1.2.3
cmp -s "$tmp/out/libfoo-1.2.3-1-arm64.hpkg" "$tmp/first.hpkg" && pass "mock hpkg is deterministic" || fail "mock hpkg is deterministic"
clean_out
expect 1 "mock fail port" "$IMG" mock x86_64 failing 1.0
expect 1 "mock RENKUPORTS_MOCK_FAIL" -e RENKUPORTS_MOCK_FAIL=libbar "$IMG" mock x86_64 libbar 1.0
[ -z "$(ls "$tmp/out")" ] && pass "failed mock writes no hpkg" || fail "failed mock writes no hpkg"
expect 3 "mock infra port" "$IMG" mock x86 infrabroken 1.0
expect 5 "mock badimage port" "$IMG" mock x86 badimagefoo 1.0

echo "# guest images"
expect 5 "placeholder manifest: build" "$IMG" build x86 libfoo 1.0
grep -q "no builder image published for x86" "$tmp/log" && pass "placeholder message" || fail "placeholder message"
expect 5 "placeholder manifest: fetch-images" "$IMG" fetch-images x86

echo "# guest ssh key"
pubkey() { docker run --rm -v "$1:/cache" --entrypoint sh "$IMG" -c 'stat -c %a /cache/ssh/id_ed25519 && cat /cache/ssh/id_ed25519.pub'; }
k1=$(pubkey "$vol")
[ "$(head -1 <<< "$k1")" = 600 ] && grep -q '^ssh-ed25519 ' <<< "$k1" && pass "build created a 0600 ed25519 key" || fail "build created a 0600 ed25519 key"
expect 5 "second build on the same volume" "$IMG" build arm64 libfoo 1.0
[ "$(pubkey "$vol")" = "$k1" ] && pass "key persists on one volume" || fail "key persists on one volume"
docker run --rm -v "$vol2:/cache" "$IMG" build arm64 libfoo 1.0 > /dev/null 2>&1
k2=$(pubkey "$vol2")
[ -n "$k2" ] && [ "$k2" != "$k1" ] && pass "two volumes get different keys" || fail "two volumes get different keys"

docker run --rm --entrypoint sh -v "$tmp/img:/t" "$IMG" -c 'printf "fake disk" > /t/disk.qcow2 && xz -k /t/disk.qcow2' || fail "make test image"
sha=$(docker run --rm --entrypoint sh -v "$tmp/img:/t" "$IMG" -c 'sha256sum < /t/disk.qcow2.xz | cut -d" " -f1')
disksha=$(docker run --rm --entrypoint sh -v "$tmp/img:/t" "$IMG" -c 'sha256sum < /t/disk.qcow2 | cut -d" " -f1')
bad=0000000000000000000000000000000000000000000000000000000000000000
manifest() { printf '{"version":1,"images":{"arm64":{"url":"file:///t/disk.qcow2.xz","sha256":"%s","diskSha256":"%s","bytes":0,"haikuRev":"test"}}}' "$1" "$2" > "$tmp/img/m.json"; }
manifest "$bad" "$disksha"
expect 5 "SHA-256 mismatch of the download" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" fetch-images arm64
manifest "$sha" "$bad"
expect 5 "SHA-256 mismatch of the decompressed disk" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" fetch-images arm64
echo '{"version":1,' > "$tmp/img/broken.json"
expect 5 "broken manifest" -e RENKUPORTS_IMAGES_MANIFEST=/t/broken.json "$IMG" fetch-images arm64
manifest "$sha" "$disksha"
expect 0 "verified download" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" fetch-images arm64
expect 0 "info after download" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" info --json
jq -e '.guestImages.arm64 == "cached"' "$tmp/log" > /dev/null && pass "arm64 image cached" || fail "arm64 image cached"
expect 0 "gc" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" gc
expect 0 "gc keeps the current image" -e RENKUPORTS_IMAGES_MANIFEST=/t/m.json "$IMG" info --json
jq -e '.guestImages.arm64 == "cached"' "$tmp/log" > /dev/null && pass "image still cached after gc" || fail "image still cached after gc"

echo "# lock and docker stop"
docker run -d --init --name "$job" -v "$vol:/cache" -v "$tmp/out:/out" -e RENKUPORTS_MOCK_SLEEP=600 -e RENKUPORTS_GRACE=10 \
	"$IMG" mock arm64 slowport 1.0 > /dev/null
for _ in $(seq 50); do docker logs "$job" 2>&1 | grep -q "mock build of slowport" && break; sleep 0.2; done
expect 4 "lock contention" "$IMG" mock arm64 other 1.0
start=$(date +%s)
docker stop -t 90 "$job" > /dev/null
code=$(docker wait "$job") elapsed=$(($(date +%s) - start))
docker logs "$job" > "$tmp/log" 2>&1
[ "$code" = 75 ] && [ "$elapsed" -le 10 ] && pass "docker stop exits 75 within the grace (${elapsed}s)" || fail "docker stop (exit $code after ${elapsed}s)"
expect 0 "lock is released after stop" "$IMG" mock arm64 other 1.0
docker rm "$job" > /dev/null

echo "# build up to the guest boot (blank disk, so no ssh), then docker stop"
docker run --rm --entrypoint sh -v "$tmp/img:/t" "$IMG" -c 'set -e; cd /t; qemu-img create -q -f qcow2 boot.qcow2 64M; xz -k boot.qcow2
	git init -q -b main ports; mkdir -p ports/a/foo; echo x > ports/a/foo/foo-1.0.recipe
	git -C ports add .; git -C ports -c user.email=t@t -c user.name=t commit -qm init
	printf "{\"version\":1,\"images\":{\"arm64\":{\"url\":\"file:///t/boot.qcow2.xz\",\"sha256\":\"%s\",\"diskSha256\":\"%s\"}}}" \
		"$(sha256sum < boot.qcow2.xz | cut -d" " -f1)" "$(sha256sum < boot.qcow2 | cut -d" " -f1)" > boot.json' || fail "make boot fixture"
docker run -d --init --name "$job" -v "$vol2:/cache" -v "$tmp/out:/out" -v "$tmp/img:/t" -e RENKUPORTS_IMAGES_MANIFEST=/t/boot.json \
	-e RENKUPORTS_PORTS_URL=file:///t/ports -e RENKUPORTS_GRACE=2 -e RENKUPORTS_GUEST_MB=256 -e RENKUPORTS_ACCEL=tcg \
	"$IMG" build arm64 foo 1.0 > /dev/null
for _ in $(seq 100); do docker logs "$job" 2>&1 | grep -q "booting the arm64 guest" && break; sleep 0.3; done
seedkey=$(docker exec "$job" sh -c 'mlabel -s -i /run/seed.img :: && mtype -i /run/seed.img ::authorized_keys' 2>&1)
grep -q RENKUSEED <<< "$seedkey" && grep -qF "$(tail -1 <<< "$k2")" <<< "$seedkey" \
	&& pass "seed disk RENKUSEED holds the volume's public key" || { echo "$seedkey" > "$tmp/log"; fail "seed disk RENKUSEED holds the volume's public key"; }
sleep 2
docker exec "$job" sh -c 'grep -l qemu-system-aarch64 /proc/[0-9]*/cmdline' > /dev/null 2>&1 && pass "QEMU runs with the seed disk" || fail "QEMU runs with the seed disk"
docker stop -t 90 "$job" > /dev/null
code=$(docker wait "$job")
docker logs "$job" > "$tmp/log" 2>&1
[ "$code" = 75 ] && grep -q "stopping QEMU" "$tmp/log" && pass "stop with a running guest exits 75" || fail "stop with a running guest (exit $code)"
docker rm "$job" > /dev/null

echo "# build with stub ssh and socat that record their commands (QEMU boots the blank disk)"
mkdir -p "$tmp/img/bin"
printf '#!/bin/sh\necho "$*" >> /t/ssh.log\ncat > /dev/null\n' > "$tmp/img/bin/ssh"
printf '#!/bin/sh\ncat >> /t/qmp.log\n' > "$tmp/img/bin/socat"
chmod 755 "$tmp/img/bin/ssh" "$tmp/img/bin/socat"
docker run --rm --init -v "$vol2:/cache" -v "$tmp/out:/out" -v "$tmp/img:/t" -e PATH=/t/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin -e RENKUPORTS_IMAGES_MANIFEST=/t/boot.json \
	-e RENKUPORTS_PORTS_URL=file:///t/ports -e RENKUPORTS_GRACE=2 -e RENKUPORTS_GUEST_MB=256 -e RENKUPORTS_ACCEL=tcg \
	"$IMG" build arm64 foo 1.0 > "$tmp/log" 2>&1
code=$?
[ "$code" = 1 ] && grep -q "produced no package" "$tmp/log" && pass "stub build exits 1 without a package" || fail "stub build (exit $code)"
grep -q "PYTHONUNBUFFERED=1 haikuporter " "$tmp/img/ssh.log" && pass "haikuporter runs with PYTHONUNBUFFERED=1" \
	|| fail "haikuporter runs with PYTHONUNBUFFERED=1"
grep -q '"quit"' "$tmp/img/qmp.log" && ! grep -q system_powerdown "$tmp/img/qmp.log" \
	&& pass "arm64 power-off sends QMP quit, not system_powerdown" || fail "arm64 power-off sends QMP quit, not system_powerdown"

echo "# QEMU argv"
for a in arm64 x86_64 x86; do
	for c in kvm tcg; do
		dr "$IMG" qemu-argv "$a" "$c" > "$tmp/argv" 2>&1
		diff -u "$here/golden/qemu-$a-$c.txt" "$tmp/argv" > "$tmp/log" && pass "argv $a $c" || fail "argv $a $c"
	done
	# QEMU accepts the TCG command line (it runs until the timeout with an empty disk).
	dr --entrypoint bash -e RENKUPORTS_GUEST_MB=256 -e a="$a" "$IMG" -c 'mkdir -p /cache/work /cache/logs
		qemu-img create -q -f qcow2 /cache/work/$a.qcow2 64M && cp /usr/share/AAVMF/AAVMF_VARS.fd /cache/work/arm64-vars.fd
		mkfs.vfat -C -n RENKUSEED /run/seed.img 1440 > /dev/null
		mapfile -t q < <(renkuports-build qemu-argv $a tcg); timeout 3 "${q[@]}"; [ $? = 124 ]' > "$tmp/log" 2>&1 \
		&& pass "QEMU starts with the $a tcg argv" || fail "QEMU starts with the $a tcg argv"
done
dr "$IMG" gc > /dev/null 2>&1

echo
[ $fails = 0 ] && echo "all tests passed" || echo "$fails test(s) failed"
[ $fails = 0 ]
