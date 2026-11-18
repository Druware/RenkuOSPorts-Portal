#!/bin/bash
# End-to-end test of the worker without Docker or a server: the stub API server plus the fake docker, driven by
# renkuports-builder-cli. Scenarios: a build that uploads and completes; Stop during a build (docker stop, then
# release, falling back to fail on a server without the endpoint); an infrastructure failure (exit 3, release).
# Usage: Tests/e2e/run.sh   (from clients/macos)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
pkg=$(cd "$here/../.." && pwd)
swift build --package-path "$pkg" --product renkuports-builder-cli >/dev/null
cli="$(swift build --package-path "$pkg" --show-bin-path)/renkuports-builder-cli"
tmp=$(mktemp -d)
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$tmp"' EXIT
failures=0

# scenario <name> <port name> <stub env> <cli action: wait|stop> <python check over the events list>
scenario() {
	local name=$1 port=$2 env=$3 action=$4 check=$5 dir="$tmp/$1"
	mkdir -p "$dir/work" "$dir/state"
	env $env PORT_NAME="$port" python3 -I "$here/stub_server.py" "$dir/port" "$dir/events" &
	local server=$!
	for _ in $(seq 50); do [ -s "$dir/port" ] && break; sleep 0.1; done
	cat > "$dir/settings" <<-EOF
		server=http://127.0.0.1:$(cat "$dir/port")/
		client_id=c1
		work_dir=$dir/work
		docker_path=$here/fake_docker.sh
		image=renkuports-builder:test
		poll_seconds=5
		${EXTRA_SETTINGS:-}
	EOF
	RENKUPORTS_SECRET=s3cret FAKE_DOCKER_LOG="$dir/docker.log" FAKE_DOCKER_STATE="$dir/state" \
		"$cli" --settings "$dir/settings" --once > "$dir/out.log" 2>&1 &
	local client=$!
	if [ "$action" = stop ]; then
		for _ in $(seq 100); do grep -q "SIGTERM\|guest: haikuporter" "$dir/out.log" && break; sleep 0.1; done
		kill -INT $client
	fi
	wait $client || true
	kill $server
	wait $server 2>/dev/null || true
	if python3 -I -c "import json,sys; e=[json.loads(l) for l in open(sys.argv[1])]; sys.exit(0 if ($check) else 1)" "$dir/events"; then
		echo "PASS $name"
	else
		echo "FAIL $name"; failures=$((failures + 1))
		echo "--- events"; cat "$dir/events"; echo "--- client output"; cat "$dir/out.log"; echo "--- docker calls"; cat "$dir/docker.log"
	fi
	[ -n "${VERBOSE:-}" ] && { echo "--- events"; cat "$dir/events"; echo "--- client output"; cat "$dir/out.log"; echo "--- docker calls"; cat "$dir/docker.log"; }
	return 0
}

posts() { echo "[x['path'].rsplit('/',1)[-1] + ':' + str(x['status']) for x in e if x['method']=='POST' and '/jobs/42/' in x['path']]"; }

scenario complete libfoo "FIRST_401=1" wait \
	"$(posts) == ['packages:200', 'complete:200'] and any(x.get('forced') for x in e) and [x['file'] for x in e if 'file' in x] == ['libfoo-1.2-1-arm64.hpkg']"
scenario stop-release-404 slowport "RELEASE_404=1" stop \
	"$(posts) == ['release:404', 'fail:200'] and [x['summary'] for x in e if x['path'].endswith('/fail')] == ['infrastructure: client stopped'] and 'stop -t 90 renkuports-job-42' in open(sys.argv[1][:-6] + 'docker.log').read()"
scenario infra-release infraport "" wait \
	"$(posts) == ['release:204'] and [x['reason'] for x in e if x['path'].endswith('/release')] == ['infrastructure failure (exit 3)']"
scenario build-fail failport "" wait \
	"$(posts) == ['fail:200'] and [x['summary'] for x in e if x['path'].endswith('/fail')] == ['build failed (exit 1)']"

# The build_command= test hook: no pre-flight, no container (docker_path points at a missing file to prove it).
EXTRA_SETTINGS="build_command=$pkg/../../client/scripts/mock-build.sh
docker_path=/nonexistent/docker" scenario build-command libbar "" wait \
	"$(posts) == ['packages:200', 'complete:200'] and [x['file'] for x in e if 'file' in x] == ['libbar-1.2-1-arm64.hpkg']"

[ $failures -eq 0 ] && echo "all e2e scenarios passed" || { echo "$failures scenario(s) failed"; exit 1; }
