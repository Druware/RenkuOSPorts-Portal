# RenkuPorts Builder for macOS

A SwiftUI build client for the RenkuOSPorts coordinator. It works like the Haiku client in `clients/haiku`, but builds each job in the `renkuports-builder` container (`docs/linux-mac-build-clients.md`, sections A and E) through Docker Desktop (or OrbStack) instead of running HaikuPorter on the host. It needs macOS 14 or later and is not sandboxed, since it runs `docker`.

The package has three parts: `BuilderCore` (settings, Keychain, API client, pre-flight, the build worker; no UI), the `RenkuPortsBuilder` app, and `renkuports-builder-cli`, which runs the same worker without a window.

## Build

With Xcode 16 or later (Swift 6) installed:

    cd clients/macos
    swift build -c release
    Scripts/make-app.sh          # writes .build/app/RenkuPorts Builder.app (ad-hoc signed)

`make-app.sh` wraps the release binary in an app bundle with an Info.plist and a placeholder icon drawn by `Scripts/make-icon.swift`. Copy the app to `/Applications` to keep it. `swift run RenkuPortsBuilder` also opens the window, without a bundle.

Style: `.swiftformat` and `.swiftlint.yml` in this directory; run `swiftformat . && swiftlint --strict` before a commit.

## Configure

1. In the portal, open Clients and create a build client with a name and a platform (`arm64` on Apple Silicon, `x86_64` on Intel; any platform the image lists works, since the guest is emulated). Copy the id and the secret; the secret is shown only once. `riscv64` clients are refused: there is no riscv64 builder guest yet.
2. Install and start Docker Desktop, and raise its Memory setting to at least `guest_mb` plus 1 GiB (Settings, Resources).
3. Open RenkuPorts Builder. With no settings saved yet it opens on the Settings tab. Save writes the settings and stores the secret.

Settings are `key=value` lines in `~/Library/Application Support/RenkuPortsBuilder/settings` (mode 0600). The secret is not in that file: it is a generic password in the login Keychain (service `org.renkuos.PortsBuilder`, account `client-secret`), which Keychain Access shows and can delete.

| Key | Default | Meaning |
|---|---|---|
| `server`, `client_id` | `http://localhost:8080/`, empty | the portal and the client's id; give the final URL, redirects are not followed |
| `docker_path` | `docker` | also looked up in `/usr/local/bin`, `/opt/homebrew/bin`, `/Applications/Docker.app/Contents/Resources/bin` and `~/.orbstack/bin` (apps do not get the shell's PATH) |
| `image` | `ghcr.io/druware/renkuports-builder:<version>` | the builder image |
| `kvm` | `auto` | `auto` never passes `/dev/kvm` on macOS (Docker Desktop's VM has none), `on` passes it (pre-flight fails without it), `off` forces TCG |
| `cache_volume` | `renkuports-cache` | one build per volume; two clients on one host need two volumes |
| `guest_mb`, `jobs` | `8192`, `4` | `RENKUPORTS_GUEST_MB`, `RENKUPORTS_JOBS` |
| `pull_policy` | `if-missing` | `always` runs `docker pull` before each job |
| `work_dir` | `~/Library/Application Support/RenkuPortsBuilder/work` | `job-<id>/build.log` (kept) and `job-<id>/out` (removed after the job); must be under a path Docker Desktop shares (home is shared by default) |
| `run_on_start` | `false` | Start when the app opens |
| `poll_seconds` | `60` | poll interval while idle, at least 5 (no field in the window) |
| `build_command` | none (no field in the window) | test hook: `<cmd> <port> <version> <outdir>` instead of the container, with `RENKUPORTS_PLATFORM`, `RENKUPORTS_JOB_ID` and `RENKUPORTS_RECIPE_PATH` set; no pre-flight. `client/scripts/mock-build.sh` works as such a command |

Test Connection gets a token and shows the client, its owner, the queued jobs for its platform and the job it holds. Check Docker runs the pre-flight as a checklist without pulling or downloading anything.

## Run

The Status tab shows the state (connecting, idle, building, paused, stopped, or the last error), the current job with its elapsed time, the queue line from the server, the live build output and the recent results (newest first). While a job runs the state names the platform, the accelerator (`KVM` or `TCG (slow)`), the image and "guest booting" until the guest's first output. Apple Silicon builds arm64 under TCG inside Docker Desktop, which is slow (hours for large ports).

- Start: runs the pre-flight, then polls for jobs; or resumes after Pause.
- Pause: lets the current job finish its normal path (including the wait and retry after exit 4), then stops claiming.
- Stop: aborts the HTTP request in flight (an upload, for example), stops the container with `docker stop -t 90 renkuports-job-<id>` (`docker kill` after 100 s), gives the job back to the queue and stops. Quitting the app during a job does the same first.

At launch the app lists `renkuports-job-*` containers left running by an earlier session and offers to stop them, since they hold the cache volume.

## What a job does

On Start the client reads `/api/client/me` and runs the pre-flight: Docker and its daemon (`docker version`), the image (pulled if missing), the image's `info --json` with the cache volume mounted (its arch must match the daemon's, the client's platform must be among its targets, the guest image is downloaded with `fetch-images` if missing), Docker's memory (a warning only) and that no other `renkuports-job-*` container uses the cache volume. A failure pauses the client with the message.

Then for each job:

1. Claims it (`POST /api/client/jobs/next`). The claim is never aborted by Stop: if Stop arrives while it is in flight and the job comes back, the job is released at once without building.
2. Creates `work_dir/job-<id>/out`, opens `build.log`, reads `portsRepo` from `/api/client/me` (without one the job fails: the container cannot clone the tree) and runs, as in spec section A:

        docker run --rm --init --name renkuports-job-<id> -v <cache_volume>:/cache -v <out>:/out \
          -e RENKUPORTS_JOB_ID=... -e RENKUPORTS_PLATFORM=... -e RENKUPORTS_RECIPE_PATH=... \
          -e RENKUPORTS_PORTS_URL=... -e RENKUPORTS_PORTS_REF=... -e RENKUPORTS_JOBS=... \
          -e RENKUPORTS_GUEST_MB=... -e RENKUPORTS_ACCEL=... <image> build <arch> <port> <version>

   with stdout and stderr into `build.log` and the window (flushed every 300 ms or 32 KB).
3. Acts on the exit code (below). Uploading takes every `*.hpkg` in `out/` except `-source.hpkg`, each as multipart with the `sha256` and `buildDate` fields before the `file` part, streamed from a temporary body file. Complete, fail and release send the last 5 MB of `build.log`.
4. Removes `out/` and keeps `build.log`.

| Exit code | Meaning | What the client does |
|---|---|---|
| 0 | built | upload, then `complete` (no `.hpkg` at all fails the job) |
| 1 | build failed | `fail`, summary `build failed (exit 1)` |
| 2 | bad arguments or environment (a client bug) | `fail`, then pause the client |
| 3 | infrastructure failure (guest, clone, disk, KVM, QEMU) | release |
| 4 | cache volume busy | wait 30 s and run once more (Pause does not cut the wait short); a second 4 is released |
| 5 | guest image missing or bad | release, "run Prepare" |
| 75 | stopped by `docker stop` | release, `client stopped` |
| 125 to 127, 137 | docker failed or the container was killed | release |
| other | unexpected | `fail`, `container exited with code N` |

Release is `POST /api/client/jobs/{id}/release` with `{"reason": "...", "log": "..."}`, which puts the job back in the queue. A server without that endpoint answers 404; the client then calls `fail` with the summary `infrastructure: <reason>`. Stop always releases, whatever the exit code. Tokens are refreshed before their 12 h lifetime ends and on a 401 (the request is retried once).

## Command line

`renkuports-builder-cli` (in `.build/release` or `.build/debug`) runs the same worker without a window and prints the log and the state changes to stdout:

    renkuports-builder-cli [--settings <file>] [--once]

It reads the settings file (default as above) and the secret from `RENKUPORTS_SECRET`, else the Keychain. `--once` pauses as soon as a job starts, so it exits after that job is reported (or right away when the pre-flight fails). SIGINT and SIGTERM are Stop.

## Testing

    swift test           # BuilderCore unit tests (Swift Testing), no Docker, no server
    Tests/e2e/run.sh     # end-to-end: the CLI against a stub server and a fake docker

The unit tests cover the settings file, the Keychain (under a test-only service name, removed afterwards; skipped when the login keychain is not writable), JSON escaping, the multipart layout, the exit-code mapping, the `docker run` arguments, `.hpkg` collection, the riscv64 refusal, and against a `URLProtocol` stub the 401 retry, release and its fallback to fail on 404, cancelling a request, Stop during a claim, and Pause and Stop during the exit 4 wait (the latter two with the fake docker).

`Tests/e2e/run.sh` starts `stub_server.py` (one job; it checks each upload's part order, SHA-256 and `buildDate`) and runs the CLI with `docker_path=Tests/e2e/fake_docker.sh`, which answers the pre-flight and emulates the container (`fail*` ports exit 1, `infra*` exit 3, `busy*` exit 4 once, `slow*` run until `docker stop`). Scenarios: build, upload and complete (with a forced 401); Stop during a build against a server without the release endpoint; exit 3 released; exit 1 failed; and the `build_command` hook with `client/scripts/mock-build.sh`. `VERBOSE=1` prints each scenario's requests, client output and docker calls.

To try the real container, set `image` to a locally built `renkuports-builder:dev` (see `containers/builder`).
