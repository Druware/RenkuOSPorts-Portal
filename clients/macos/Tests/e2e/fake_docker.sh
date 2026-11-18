#!/bin/bash
# Fake docker for the end-to-end test (docker_path=<this script>). Logs each call to $FAKE_DOCKER_LOG.
# run ... info --json: prints an arm64 image info. run ... build <arch> <port> <version>: behaves like the
# container's mock subcommand: writes <port>-<version>-1-<arch>.hpkg (and a -source.hpkg the client must skip) to
# the /out mount; a port starting with "fail" exits 1, "infra" exits 3, "slow" runs until docker stop (exit 75),
# "busy" exits 4 on the first run of a job and builds on the second.
set -u
echo "$*" >> "${FAKE_DOCKER_LOG:-/dev/null}"
state=${FAKE_DOCKER_STATE:-/tmp}
case "$1" in
version) echo arm64 ;;
image | pull) ;;
info) echo 17179869184 ;;
ps) ;;
stop) kill -TERM "$(cat "$state/$4.pid")" ;;
kill) kill -KILL "$(cat "$state/$2.pid")" ;;
run)
	shift
	name="" out=""
	while [ $# -gt 0 ]; do
		case "$1" in
		--name) name=$2; shift 2 ;;
		-v) [[ $2 == *:/out ]] && out=${2%:/out}; shift 2 ;;
		-e | --device) shift 2 ;;
		--*) shift ;;
		*) break ;;
		esac
	done
	image=$1 cmd=$2
	shift 2
	if [ "$cmd" = info ]; then
		echo '{"version":"0.1.1","imageArch":"aarch64","targets":["arm64","x86_64","x86"],"accel":{"arm64":"tcg","x86_64":"tcg","x86":"tcg"},"guestImages":{"arm64":"cached","x86_64":"missing","x86":"missing"}}'
		exit 0
	fi
	arch=$1 port=$2 version=$3
	echo "[host] booting $arch guest ($image)"
	echo "guest: haikuporter $port-$version"
	case "$port" in
	fail*) echo "error: build failed"; exit 1 ;;
	infra*) echo "[host] guest did not boot"; exit 3 ;;
	busy*)
		# Exit 4 (cache volume busy) on the first run of the job, success on the retry.
		count="$out/../busy.count"
		[ -e "$count" ] || { touch "$count"; echo "[host] cache volume busy"; exit 4; } ;;
	slow*)
		echo $$ > "$state/$name.pid"
		trap 'echo "[host] SIGTERM: shutting the guest down"; exit 75' TERM
		while true; do sleep 1 & wait $!; done ;;
	esac
	printf 'mock hpkg\nport=%s\n' "$port" > "$out/$port-$version-1-$arch.hpkg"
	echo source > "$out/$port-$version-1-source.hpkg"
	echo "[host] wrote $port-$version-1-$arch.hpkg"
	;;
*) echo "fake docker: unknown command $1" >&2; exit 125 ;;
esac
