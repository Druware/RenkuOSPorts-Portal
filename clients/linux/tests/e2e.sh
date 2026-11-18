#!/bin/bash
# End-to-end test without a display or Docker: the headless client against tests/stub_server.py with
# tests/fake-docker.sh as docker_path. Job 42 is built, uploaded and completed; job 43 (exit 3) is given back;
# job 44 hangs until SIGTERM, which must docker-stop the container and give the job back.
# Usage: e2e.sh <path to renkuports-builder>
set -euo pipefail
bin=$1
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
export FAKE_DOCKER_STATE=$tmp XDG_CONFIG_HOME=$tmp/config
python3 "$here/stub_server.py" "$tmp/port" "$tmp/events" &
server=$!
trap 'kill $server 2>/dev/null; rm -rf "$tmp"' EXIT
for _ in $(seq 50); do [ -s "$tmp/port" ] && break; sleep 0.1; done
mkdir -p "$tmp/config/renkuports-builder"
cat > "$tmp/config/renkuports-builder/settings" <<EOF
server=http://127.0.0.1:$(cat "$tmp/port")
client_id=e2e
secret=s
docker_path=$here/fake-docker.sh
image=renkuports-builder:dev
work_dir=$tmp/work
poll_seconds=5
EOF

fail() { echo "FAIL: $*"; echo "--- events"; cat "$tmp/events" || true; echo "--- docker calls"; cat "$tmp/calls" || true; exit 1; }
event() { grep -F "$1" "$tmp/events" > /dev/null || fail "no event matching $1"; }

"$bin" --headless --once
event '"call": "claim", "job": 42'
event '"call": "upload", "job": 42, "names": ["sha256", "buildDate", "file"], "file": "libfoo-1.0-1-arm64.hpkg", "sha_ok": true'
event '"call": "complete", "job": 42'
grep -F '"job": 42' "$tmp/events" | grep -F '"call": "complete"' | grep -F '"log_has_build": true' > /dev/null || fail "complete log lacks the build output"
grep -F 'source.hpkg' "$tmp/events" && fail "uploaded a -source.hpkg"
grep -F 'run --rm --init --name renkuports-job-42 -v renkuports-cache:/cache' "$tmp/calls" > /dev/null || fail "docker run line"
[ -f "$tmp/work/job-42/build.log" ] && [ ! -e "$tmp/work/job-42/out" ] || fail "work dir: build.log kept, out/ removed"
echo "ok: job 42 claimed, uploaded, completed"

"$bin" --headless --once
event '"call": "release", "job": 43, "summary": "build environment failed (exit 3)"'
echo "ok: job 43 (exit 3) released"

"$bin" --headless > "$tmp/stop.out" &
client=$!
for _ in $(seq 100); do [ -f "$tmp/renkuports-job-44.pid" ] && break; sleep 0.1; done
[ -f "$tmp/renkuports-job-44.pid" ] || fail "job 44 did not start"
kill -TERM $client
wait $client
grep -Fx 'stop -t 90 renkuports-job-44' "$tmp/calls" > /dev/null || fail "no docker stop -t 90"
event '"call": "release", "job": 44, "summary": "client stopped"'
grep -F '"call": "fail"' "$tmp/events" && fail "a job was failed"
echo "ok: job 44 stopped with docker stop and released"
