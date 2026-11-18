# RenkuPorts Builder for Linux

A GTK 4 build client for the RenkuOSPorts coordinator (C, libsoup 3, json-glib). It works like the Haiku client in `clients/haiku`, but builds each job in the `renkuports-builder` container (`docs/linux-mac-build-clients.md`, sections A and E) instead of running HaikuPorter on the host.

## Build and test

    sudo apt install libgtk-4-dev libsoup-3.0-dev libjson-glib-dev meson gcc python3
    cd clients/linux && meson setup build && meson compile -C build && meson test -C build

On a host without GTK 4 (for example macOS), use the container in `Dockerfile.dev`; its header has the commands. `meson test` runs the unit tests (`tests/test_core.c`) and an end-to-end test (`tests/e2e.sh`): the headless client against a stub server with a fake `docker`, which builds and completes one job, gives back one that exits 3, and stops one with `docker stop`.

## Run

`renkuports-builder` opens the window (Status and Settings tabs, Start, Pause, Stop, Test connection, as on Haiku). `renkuports-builder --headless` runs without a window until SIGINT or SIGTERM (which stop the build and give the job back); `--once` makes one claim attempt and exits. `data/org.renkuos.PortsBuilder.desktop` is the menu entry.

Settings are `key=value` lines in `${XDG_CONFIG_HOME:-~/.config}/renkuports-builder/settings`, mode 0600 since the file holds the secret:

| Key | Default | Meaning |
|---|---|---|
| `server`, `client_id`, `secret` | `http://localhost:8080/` | the portal and the client's credentials |
| `docker_path` | `docker` | also looked up in `/usr/local/bin`, `/usr/bin`, `/snap/bin`, `~/.local/bin`, `~/bin` |
| `image` | `ghcr.io/druware/renkuports-builder:<version>` | the builder image |
| `kvm` | `auto` | `auto` passes `/dev/kvm` when it is usable, `on` always (pre-flight fails without it), `off` never |
| `cache_volume` | `renkuports-cache` | one build per volume; two clients on one host need two volumes |
| `guest_mb`, `jobs` | `8192`, `4` | `RENKUPORTS_GUEST_MB`, `RENKUPORTS_JOBS` |
| `pull_policy` | `if-missing` | `always` runs `docker pull` before each job |
| `work_dir` | `~/.local/share/renkuports-builder/work` | `job-<id>/build.log` (kept) and `job-<id>/out` (removed after the job) |
| `run_on_start`, `poll_seconds` | `false`, `60` | as on Haiku; `poll_seconds` is at least 5 |
| `build_command` | none (no field in the window) | test hook: `<cmd> <port> <version> <outdir>` instead of the container, no pre-flight |

## What a job does

On Start the client reads `/api/client/me` and runs the pre-flight: Docker and its daemon, the image (pulled if missing), the image's `info --json` (arch, targets, guest image, downloaded with `fetch-images` if missing), Docker's memory (a warning) and that no other `renkuports-job-*` container uses the cache volume. A failure pauses the client with the message. A `riscv64` client is refused: there is no riscv64 builder guest yet. Test connection shows the same checklist without pulling or downloading.

Each job runs `docker run --rm --init --name renkuports-job-<id> ...` as in spec section A, with the output in `build.log` and the window. The exit code decides what happens: 0 uploads the `*.hpkg` files from `out/` (not `-source.hpkg`; multipart with `sha256` and `buildDate` first, streamed from disk) and completes the job with the last 5 MB of the log; 1 and other codes fail it; 2 fails it and pauses the client; 4 is retried once after 30 s; 3, 5, 125 to 127 and 137 give the job back with `POST /api/client/jobs/{id}/release` (`{"reason", "log"}`), and so does Stop, which runs `docker stop -t 90 renkuports-job-<id>` and `docker kill` after 100 s. A server without the release endpoint (404) gets `fail` with the summary `infrastructure: <reason>` instead.

At startup the window lists `renkuports-job-*` containers left running by an earlier session and offers to stop them.
