#!/bin/bash
# Fake docker for tests/e2e.sh: answers the pre-flight, runs `build` like mock-build.sh, and honours `stop`.
# Port "infra*" exits 3; port "sleeper*" hangs until `docker stop`, then exits 75. State in $FAKE_DOCKER_STATE.
set -u
state=${FAKE_DOCKER_STATE:?}
echo "$*" >> "$state/calls"
case "$1" in
version) echo arm64 ;;
image) exit 0 ;;
info) echo 17179869184 ;;
ps) ;;
stop|kill)
	name=${!#}
	[ -f "$state/$name.pid" ] && kill -TERM "$(cat "$state/$name.pid")" ;;
run)
	shift
	name="" out=""
	while [ $# -gt 0 ]; do
		case "$1" in
		--rm|--init) shift ;;
		--name) name=$2; shift 2 ;;
		-v) [[ $2 == *:/out ]] && out=${2%:/out}; shift 2 ;;
		-e|--device) shift 2 ;;
		*) break ;;
		esac
	done
	image=$1 cmd=$2
	shift 2
	case "$cmd" in
	info) echo '{"version":"0.1.1","imageArch":"arm64","targets":["arm64","x86_64","x86"],"accel":{"arm64":"tcg"},"guestImages":{"arm64":"cached"}}' ;;
	build)
		arch=$1 port=$2 version=$3
		echo "[host] $image: booting the $arch guest"
		case "$port" in
		infra*) echo "[host] guest did not boot"; exit 3 ;;
		sleeper*)
			echo $$ > "$state/$name.pid"
			trap 'echo "[host] SIGTERM, guest shut down"; exit 75' TERM
			echo "guest: building forever"
			while :; do sleep 0.2; done ;;
		esac
		echo "mock build of $port $version for $arch"
		printf 'mock hpkg\nport=%s\nversion=%s\narch=%s\n' "$port" "$version" "$arch" > "$out/$port-$version-1-$arch.hpkg"
		printf 'source\n' > "$out/$port-$version-1-source.hpkg"
		echo "wrote $port-$version-1-$arch.hpkg" ;;
	*) exit 2 ;;
	esac ;;
*) exit 125 ;;
esac
