# RenkuPorts Builder (native RenkuOS/Haiku build client)

A small native app (C++, Haiku API, signature `application/x-vnd.RenkuOS-PortsBuilder`) that runs inside a RenkuOS or Haiku machine or VM and works as a build client for the RenkuOSPorts coordinator: it claims the next job for its platform, builds the recipe with HaikuPorter in a local ports tree, uploads the packages and completes the job, or reports the failure with the build log. It speaks the same client API as the .NET client in `src/RenkuPorts.Client` (that one cannot run on Haiku). The window is deliberately plain.

## Build

On RenkuOS/Haiku with `haiku_devel`, `makefile_engine`, `gcc` and `make` installed:

    cd clients/haiku
    make                      # binary: objects.<arch>-<cc>-release/RenkuPortsBuilder
    make OBJ_DIR=objects      # or a fixed output directory
    make install              # copies it to ~/config/non-packaged/apps

It links `libbe`, `libbnetapi`, the static `libnetservices.a` (HTTP) and `libshared.a` (JSON), all part of `haiku_devel`. `renkuports_builder.recipe` is a draft HaikuPorts recipe for packaging it later (it needs a source commit and checksum, and a `renkuports_builder-<version>.recipe` file name, before it goes into the ports tree).

## Configure

1. In the portal, open Clients and create a build client with a name and the platform of this machine (for example `arm64`). Copy the id and the secret; the secret is shown only once.
2. Have a RenkuOSPorts checkout (a git clone, so the client can update it) and a HaikuPorter config file whose `TREE_PATH` points at it, for example `~/config/settings/haikuports.conf` with `TREE_PATH`, `PACKAGER` and `TARGET_ARCHITECTURE`.
3. Start RenkuPorts Builder. With no settings saved yet it opens on the Settings tab:

| Field | Meaning |
|---|---|
| Server URL | The portal, e.g. `https://ports.example.org/` (http works for a local test server). Redirects are not followed, so give the final URL. |
| Client id, Secret | From step 1. |
| Ports tree | The RenkuOSPorts checkout. Packages are collected from its `packages/` directory. |
| HaikuPorter command | `haikuporter` or a full path. |
| HaikuPorter config | Passed as `--config=`; its `TREE_PATH` must be the ports tree above. |
| Jobs (-j) | Passed to HaikuPorter as `-j<n>`. |
| Work directory | Per-job directories `job-<id>/` with `build.log`. |
| Start building when the application starts | Runs Start on launch, e.g. with the app in `~/config/settings/boot/launch`. |

Save writes `~/config/settings/RenkuPortsBuilder` (key=value lines, mode 0600 since it holds the secret). Test connection gets a token and reads `/api/client/me`, and shows the client, its owner, how many jobs are queued for its platform and the job it holds, if any.

## Run

The Status tab shows the state (connecting, idle, building, paused, stopped, or the last error), the current job with its elapsed time, the queue line from the server, the live build output and the recent results (newest first).

- Start: begins polling (every 60 s while idle), or resumes after Pause.
- Pause: lets the current build finish and report, then stops claiming jobs.
- Stop: interrupts the running build (SIGINT to its process group, so HaikuPorter can clean up its chroot; SIGKILL after 60 s), reports the job as failed with "client stopped" so it is not left claimed, and stops. Quitting the window during a build does the same first.

The buttons can also be pressed from a shell: `hey application/x-vnd.RenkuOS-PortsBuilder 'strt'` (or `'paus'`, `'stop'`, `'tst_'`).

## What a job does

1. Claims the job (`POST /api/client/jobs/next`).
2. If the ports tree is a git checkout, runs `git pull --ff-only` and logs the commit it builds (the job names no commit, so this builds the branch tip; a failed pull is logged and the tree is built as it is). It warns if the job's recipe path is missing from the tree.
3. Runs `haikuporter --config=<config> -y --no-source-packages --get-dependencies -j<n> <port>-<version>` with output to `build.log` and the window.
4. Collects the `*.hpkg` files HaikuPorter added to or rewrote in `packages/` during the run (the port's packages and any dependencies built with it; `*-source.hpkg` excluded). If HaikuPorter exited 0 but wrote nothing because the packages already existed, it takes `<port>-<version>-*` and `<port>_*-<version>-*` from `packages/`, as `client/scripts/build-stack-arm64.sh` does.
5. Uploads each one as multipart (`sha256` and `buildDate` fields before the `file` part), streamed from disk with its SHA-256 computed by `sha256sum`.
6. Completes the job with the last 5 MB of the log, or fails it with a summary (non-zero exit, no packages, upload error) and the log.

Tokens are refreshed before their 12 h lifetime ends and on a 401 (the request is retried once), like the .NET client.

## Testing without HaikuPorter

A `build_command=<path>` line in the settings file (there is no field for it in the window) replaces HaikuPorter with `<path> <port> <version> <outdir>`, the .NET client's build command contract: the packages are taken from `<outdir>`, the environment has `RENKUPORTS_PLATFORM`, `RENKUPORTS_JOB_ID` and `RENKUPORTS_RECIPE_PATH`, and the git update is skipped. `client/scripts/mock-build.sh` works as such a command. `poll_seconds=<n>` (at least 5) changes the poll interval.

## Design notes

- HTTP: Haiku's legacy network services kit (`BHttpRequest` from `libnetservices.a`, as HaikuDepot uses). It streams a request body from any `BDataIO` with a caller-set `Content-Type`, which the multipart upload needs, and does HTTPS through `libbnetapi`. The newer netservices2 `BHttpRequest::SetRequestBody` rejects a MIME type with parameters, so it cannot send `multipart/form-data; boundary=...`, and libcurl's headers are not part of a base install. The legacy kit sets no socket timeout: a server that stops answering mid-request blocks the worker until Stop.
- JSON: `BJson::Parse` into a `BMessage` for replies; request bodies are written by hand (the log is escaped, and bytes that are not valid UTF-8 become `?`).
- Threads: the window only displays; a worker thread runs the poll, build and upload loop and sends it messages. Builds run as child processes in their own process group, read through a non-blocking pipe.
