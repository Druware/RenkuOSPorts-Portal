#!/bin/bash
# BuildCommand for renkuports-client on the arm64 Mac: builds one port on the HaikuPorter buildmaster
# stack (RenkuOS-on-Parallels/build-stack, Parallels VM RenkuOS-build as the builder) and copies that
# port's hpkgs to <outdir>. Prints the buildmaster output and the port's build log(s) (the client keeps
# stdout+stderr as the job log). Exit 0 only if the port built (or its hpkgs already existed).
# Usage: build-stack-arm64.sh <port> <version> <outdir>
# Env: BUILD_STACK (default below), WAIT_SECONDS=60 (poll interval while the stack is busy).
# Like run.sh it brings up the VM (setting its clock), the containers and the builder IP first, and waits
# while run.sh or another buildmaster run is active. SIGINT/SIGTERM (the client stopping) runs pause.sh,
# which stops the master's run and the build on the VM.
# Dependencies the master builds first stay in its packages/ (they are not copied out).
set -euo pipefail
[ $# -eq 3 ] || { echo "usage: $0 <port> <version> <outdir>" >&2; exit 2; }
port=$1 version=$2
mkdir -p "$3"
out=$(cd "$3" && pwd)
stack=${BUILD_STACK:-/Users/arsatori/Sources/local/RenkuOS/RenkuOS-on-Parallels/build-stack}
cd "$stack"
vm=../build-vm
runs=/var/buildmaster/output/buildruns
dc="docker compose exec -T master"

# Bring-up, as in run.sh.
[ "$(prlctl list -o status RenkuOS-build | tail -1)" != suspended ] || prlctl resume RenkuOS-build
"$vm/vm-up.sh" > /dev/null
docker compose up -d
ip=$(PRINT_IP=1 "$vm/ssh.sh")
$dc sh -c "cd buildmaster/builders && sed -i 's/\"host\": \"[^\"]*\"/\"host\": \"$ip\"/' renku-arm64-1.json \
	&& sort -u -o known_hosts known_hosts /run/secrets/builder_known_hosts"

# One buildmaster run at a time: wait for run.sh and any active run (same checks as run.sh).
busy() {
	{ [ -f state/run.pid ] && kill -0 "$(cat state/run.pid)" 2> /dev/null; } ||
		$dc sh -c 'grep -qs "build-maste[r]" /proc/[0-9]*/cmdline'
}
while busy; do
	echo "$(date '+%F %T') build stack busy (run.sh or another buildmaster run), waiting"
	sleep "${WAIT_SECONDS:-60}"
done

# Drop hpkgs a killed earlier run left half-downloaded (results.py does that, as on run.sh's start).
$dc python3 - < results.py | grep '^removed' || true

before=$($dc cat $runs/last_buildrun 2> /dev/null || echo 0)
trap '"$stack/pause.sh"; exit 130' INT TERM
echo "$(date '+%F %T') buildmaster build $port-$version (builder $ip)"
# In the background so the trap runs at once; a failed port makes buildmaster exit 4, status.json decides.
$dc buildmaster build "$port-$version" 2>&1 &
wait $! || true
trap - INT TERM

run=$($dc cat $runs/last_buildrun 2> /dev/null || echo 0)
[ "$run" != "$before" ] || { echo "buildmaster started no buildrun for $port-$version" >&2; exit 1; }

# This port's entry in the run's status.json: "<state>|<build numbers>|<hpkgs>|<reason>"
# ("|", not a tab: read would merge empty tab-separated fields).
result=$($dc python3 - "$runs/$run/status.json" "$port" << 'EOF'
import json, sys
status = json.load(open(sys.argv[1]))
for state in ('complete', 'failed', 'lost', 'skipped', 'blocked', 'active', 'scheduled'):
	for b in status['builds'].get(state, []):
		if b['port']['name'] == sys.argv[2]:
			print('|'.join([state, ' '.join(map(str, b.get('buildNumbers', []))),
				' '.join(b.get('resultingPackages', [])), b.get('reason', '').replace('\n', ' ')]))
			raise SystemExit
print(f"missing|||not in buildrun (run state: {status['buildStatus']})")
EOF
)
# Dependencies the master built first in this run are uploaded with the port, so they reach the portal too.
deps=$($dc python3 -c "import json, sys; print(' '.join(p for b in json.load(open(sys.argv[1]))['builds'].get('complete', []) if b['port']['name'] != sys.argv[2] for p in b.get('resultingPackages', [])))" "$runs/$run/status.json" "$port")
IFS='|' read -r state builds hpkgs reason <<< "$result"

for n in $builds; do
	echo "===== buildrun $run build $n log ====="
	$dc cat "$runs/$run/builds/$n.log" || true
done

ok=0
case $state in
complete) ok=1 ;;
skipped) [[ $reason == "some packages already exist"* ]] && ok=1 ;;
esac
if (( ! ok )); then
	echo "===== buildrun $run master.log (tail) ====="
	$dc tail -n 100 "$runs/$run/master.log" || true
	echo "$port-$version: $state${reason:+: $reason} (buildrun $run)" >&2
	exit 1
fi

# A skipped port's entry may list no resultingPackages: take its existing hpkgs (and subpackages such as
# <port>_devel) of this version from packages/, without source packages.
if [ -z "$hpkgs" ]; then
	hpkgs=$($dc sh -c 'cd /var/buildmaster/haikuports/packages && ls -- "$1-$2"-*.hpkg "$1"_*-"$2"-*.hpkg 2> /dev/null | grep -v -- "-source\.hpkg$"' sh "$port" "$version" | tr '\n' ' ') || true
	[ -n "$hpkgs" ] || { echo "$port-$version: $state, but no $port-$version hpkgs in the master's packages/ (buildrun $run)" >&2; exit 1; }
fi
[ -z "$deps" ] || { echo "dependencies built in buildrun $run: $deps"; hpkgs="$hpkgs $deps"; }
# shellcheck disable=SC2086 # hpkgs is a space-separated list of file names
$dc tar -C /var/buildmaster/haikuports/packages -cf - $hpkgs | tar -C "$out" -xf -
for h in $hpkgs; do [ -s "$out/$h" ] || { echo "missing $h after copy" >&2; exit 1; }; done
echo "$port-$version: $state (buildrun $run), copied: $hpkgs"
