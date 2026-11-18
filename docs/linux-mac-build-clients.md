# Container build clients for Linux and macOS

Status: spec, 2026-11-18. Implementers can build the container (section A to C), the macOS app and the Linux app in parallel against the contract in section A. Baseline is the Haiku client in `clients/haiku` (working tree), the build command contract in `README.md`, and the client endpoints in `src/RenkuPorts.Server/Endpoints/BuildClientApi.cs` and `BuildClientQueueApi.cs` (`POST /api/client/token`, `GET /api/client/me`, `GET /api/client/jobs`, `POST /api/client/jobs/next`, `POST .../{id}/packages`, `POST .../{id}/complete`, `POST .../{id}/fail`). No server change is required except the optional release endpoint in section G.

Decisions taken (not reopened): builds run in Docker; two images (x86_64 hosts, arm64 hosts), each able to build arm64, x86_64, x86 and riscv64 by booting a Haiku/RenkuOS guest under QEMU and building inside it; guest disk images come from pinned URLs, SHA-256 verified, cached in a volume; two native clients (macOS SwiftUI, Linux GTK) modelled on the Haiku client.

## A. Container CLI contract

One command line, built identically by both clients:

    docker run --rm --init --name renkuports-job-<jobId> \
      [--device /dev/kvm] \
      -v <cacheVolume>:/cache -v <outdir>:/out \
      -e RENKUPORTS_JOB_ID=<jobId> -e RENKUPORTS_PLATFORM=<platform> -e RENKUPORTS_RECIPE_PATH=<recipePath> \
      -e RENKUPORTS_PORTS_URL=<portsRepo.url> -e RENKUPORTS_PORTS_REF=<portsRepo.branch> \
      -e RENKUPORTS_JOBS=<n> -e RENKUPORTS_GUEST_MB=<mb> -e RENKUPORTS_ACCEL=<auto|kvm|tcg> \
      <image>:<tag> build <arch> <port> <version>

Other subcommands (same entrypoint, `/usr/local/bin/renkuports-build`):

| Subcommand | Purpose |
|---|---|
| `build <arch> <port> <version>` | the job; `<arch>` is the platform string (`arm64`, `x86_64`, `x86`, `riscv64`) |
| `info [--json]` | prints `{"version","imageArch","targets":[...],"accel":{"<arch>":"kvm\|tcg"},"guestImages":{"<arch>":"cached\|missing"}}` without booting anything; used by client pre-flight. `accel` is computed from `/dev/kvm` presence and arch match |
| `fetch-images [<arch>...]` | downloads and verifies guest images into the cache (so a first job does not spend 20 minutes on it); clients offer this as "Prepare" |
| `mock <arch> <port> <version>` | same contract as `build`, no QEMU: behaves like `client/scripts/mock-build.sh` (section F) |
| `gc` | removes work overlays and old images from `/cache` (manual) |

`<arch>` and `<port>` `<version>` are validated (`^[a-z0-9_]+$`, `^[A-Za-z0-9._+-]+$`); anything else exits 2.

Environment (all optional except where the subcommand needs them; `build` also reads `RENKUPORTS_PLATFORM` etc. only for logging, the positional arguments win):

| Variable | Default | Meaning |
|---|---|---|
| `RENKUPORTS_PORTS_URL` | none; required if `/cache/ports` has no clone | git URL of the ports tree (`portsRepo.url` from `/api/client/me`) |
| `RENKUPORTS_PORTS_REF` | `main` | branch, tag or commit (`portsRepo.branch`) |
| `RENKUPORTS_RECIPE_PATH` | derived by search | relative recipe path, only to warn if missing |
| `RENKUPORTS_JOBS` | `4` | `-j` for HaikuPorter and `-smp` for QEMU |
| `RENKUPORTS_GUEST_MB` | `8192` | guest RAM; the arm64 bootstrap runs used 12288. Container needs about this plus 1 GiB |
| `RENKUPORTS_ACCEL` | `auto` | `kvm` fails with exit 3 if unavailable; `tcg` forces emulation |
| `RENKUPORTS_REPO_URL` | the public RenkuOSPorts package repo | pkgman repository the guest uses for `--get-dependencies` |
| `RENKUPORTS_IMAGE_BASE_URL`, `RENKUPORTS_IMAGES_MANIFEST` | the baked-in manifest | override guest image manifest (testing) |
| `RENKUPORTS_FRESH` | `0` | `1` discards the arch's work overlay first (clean guest, much slower, see B) |
| `RENKUPORTS_GRACE` | `60` | seconds the entrypoint gives the guest to shut down on SIGTERM |
| `RENKUPORTS_MOCK_FAIL`, `MOCK_TIMESTAMP`, `MOCK_VARIANT` | | `mock` only, as in `mock-build.sh` |

Mounts:

- `/cache` (named volume, default `renkuports-cache`): `images/<arch>/` (verified downloads, read-only after verify), `work/<arch>.qcow2` (per-arch work overlay on top of the verified image), `ports/` (git clone), `logs/<arch>-serial.log` (QEMU serial, last boot), `build.lock`.
- `/out` (bind mount of the job's `out` directory): the container writes the job's `*.hpkg` here (flat, no subdirectories) and nothing else. Same meaning as `<outdir>` in the README's build command contract.

Output: stdout and stderr are the build log. Container-side messages are prefixed `[host] `; guest output (HaikuPorter over ssh) is passed through unprefixed. On exit 3 the last 100 lines of the serial log are appended to the log. The client stores the stream as `build.log` and uploads its last 5 MB, as the Haiku client does.

Exit codes:

| Code | Meaning | Client action |
|---|---|---|
| 0 | built; at least one `*.hpkg` in `/out` | upload, complete |
| 1 | build failed (HaikuPorter non-zero, or exit 0 but no hpkg for the port) | fail the job, summary `build failed (exit 1)` |
| 2 | bad arguments or env | fail with the message; this is a client bug, also pause the client |
| 3 | infrastructure failure (guest did not boot or ssh timed out, ports clone failed, out of disk, KVM requested but absent, QEMU crash) | see section G item 3: fail with prefix `infrastructure:` for now |
| 4 | cache volume busy (another build holds `build.lock`) | do not fail the job; the client should not start two containers on one volume, so this is an error in the client; wait and retry once |
| 5 | guest image missing, download failed or SHA-256 mismatch | as 3; also message "run Prepare" |
| 75 | interrupted by SIGTERM (`docker stop`) after clean guest shutdown | the client's Stop path: fail with `client stopped`, as the Haiku client |
| 125 to 127, 137 | docker itself failed, or the container was killed | infrastructure, as 3 |

Signals: the entrypoint runs under `--init` (tini is PID 1, forwards signals) and traps SIGTERM and SIGINT. On either it stops the ssh session, asks the guest to power off (`ssh shutdown`, then QMP `system_powerdown`, then QMP `quit` when `RENKUPORTS_GRACE` seconds have passed), waits for QEMU, releases the lock and exits 75. Clients call `docker stop -t 90 renkuports-job-<id>` (grace 90 s is larger than `RENKUPORTS_GRACE` 60 s so docker never sends SIGKILL in the normal case). A half-built guest is not corrupted: the work overlay is a qcow2 and the guest is shut down cleanly; if QEMU was killed the next start runs `qemu-img check` and deletes the overlay if it is dirty.

Ports tree: the entrypoint clones `RENKUPORTS_PORTS_URL` into `/cache/ports` (`git clone --depth 1 --branch <ref>`; for a commit hash `git init` plus `fetch --depth 1 origin <hash>`), otherwise `git remote set-url origin`, `fetch --depth 1 origin <ref>`, `reset --hard FETCH_HEAD`, never `git clean`. It logs the commit built. This mirrors Worker.cpp's tree update. A failed fetch with an existing clone is a warning (build the tree as it is); a failed first clone is exit 3.

Concurrency: `flock -n /cache/build.lock` at the start of `build`; the lock is held for the container's life, so one build per cache volume, and exit 4 if held. Two clients on one host use two volumes (`renkuports-cache-a`, `-b`) and need KVM or CPUs for both; the client setting is the volume name.

## B. Inside the container

Base image: `debian:trixie-slim`, pinned by digest in the Dockerfile as the Parallels build-stack does (`FROM debian:trixie-slim@sha256:...`); the digest is bumped by a deliberate commit, never by `latest`. The same Dockerfile is built per platform (`docker buildx build --platform linux/amd64,linux/arm64`), which yields the two images the user asked for as two tags of one Dockerfile plus a multi-arch manifest:

- `<registry>/renkuports-builder:<version>-amd64` and `:<version>-arm64`, and `:<version>` as the manifest list. The version is `scripts/version.sh`.
- The qemu-system packages are the same on both Debian architectures, so no Dockerfile logic differs except `TARGETARCH` selecting the KVM-capable machine defaults. The image does not contain guest images (multi-GB, downloaded on first run).

Packages (apt, `--no-install-recommends`): `qemu-system-arm qemu-system-x86 qemu-system-misc` (aarch64, x86_64, i386, riscv64), `qemu-utils`, `qemu-efi-aarch64` (AAVMF, `/usr/share/AAVMF/AAVMF_CODE.fd` and `AAVMF_VARS.fd`, as `phase1b-run.sh` uses), `u-boot-qemu` (riscv64 `u-boot.bin` as fallback, see table), `openssh-client`, `git`, `curl`, `ca-certificates`, `util-linux` (flock), `coreutils` (sha256sum), `xz-utils`, `unzip`, `jq`, `bash`, `tini`, `python3` (only if the prefetch fallback needs recipe evaluation). Host Haiku tools (`package`, `package_repo`) are not needed in the default design.

Recommendation: run HaikuPorter directly in the guest over ssh, not buildmaster plus a guest builder.

- Reasons: the Haiku client already does exactly this (`haikuporter --config ... -y --no-source-packages --get-dependencies -j<n> <port>-<version>` and collects `packages/`), so job semantics (dependencies built in the same run are uploaded, the "skipped, packages exist" rule) stay identical across all three clients. The buildmaster pattern in `RenkuOS-on-Parallels/build-stack` needs mongodb, paramiko, the Linux host `package` tools per arch, a volume init step and its own state; it exists to schedule a batch of ports across several builders, and a client only ever builds one port. It also parses recipes on the host and needs the host `package` binary for every architecture. The ssh and boot parts of that pattern are reused: fixed key, `known_hosts`, set the guest clock after boot, host-side `git` tree, `--no-git-repo`.
- Cost: dependency resolution happens in the guest, which is slow under TCG (HaikuPorter parses recipes lazily; the arm64 bootstrap notes report 4 hours to parse a whole tree in the guest under TCG versus 187 s natively). Mitigation: the work overlay persists between jobs (HaikuPorter's `repository/` dependency cache and the tree's `packages/` live in it), and `--get-dependencies` takes already published dependencies from `RENKUPORTS_REPO_URL` through pkgman instead of building them.

Guest ("builder image") contents, produced once by a RenkuOS CI job and published (section C): the OS, an `sshd` with the fixed public key in `~/config/settings/ssh/authorized_keys`, an empty `~/config/settings/Locale settings` (otherwise FirstBootPrompt hides the desktop and the boot script, as the Parallels notes found), a Haiku-side boot step that starts sshd, `haikuporter`, gcc, make, git is not required (`--no-git-repo`), `curl`/`wget` (the bootstrap guest lacked them; the builder image must have them so the guest downloads sources itself through QEMU user-mode networking), `~/config/settings/haikuports.conf` rewritten per job by the entrypoint (`TREE_PATH=/boot/home/RenkuOSPorts`, `TARGET_ARCHITECTURE=<arch, x86 becomes x86_gcc2>`, `PACKAGER="RenkuPorts Builder <noreply@renkuos.org>"`, as Worker.cpp), a pkgman repo entry for `RENKUPORTS_REPO_URL`, and a 16 GiB or larger BFS volume (images are grown with `qemu-img resize` plus `resizefs`-capable tooling; bfs_shell cannot grow, so ship the right size). sshd must have generated host keys before publication (first boot misses sshd until the key generation boot, per the Parallels notes), so the publishing CI boots the image once and shuts it down.

Per-job sequence in the entrypoint:

1. Validate args, take the lock, `qemu-img create -f qcow2 -F qcow2 -b images/<arch>/disk.qcow2 work/<arch>.qcow2` if the overlay is missing (or `RENKUPORTS_FRESH=1`). Never write to the verified base image.
2. Update `/cache/ports` (section A). Start QEMU (table below) with `-display none`, serial to `logs/<arch>-serial.log`, user-mode net with `hostfwd=tcp:127.0.0.1:2222-:22`, QMP on a unix socket. Wait up to 10 minutes (KVM) or 60 minutes (TCG) for `ssh -o ConnectTimeout=5 true` to succeed; no ssh is exit 3.
3. Set the guest clock: `ssh date -s @$(date +%s)` after every boot. arm64 and riscv64 have no RTC driver and boot at 1970 (arm64 bootstrap notes: timestamp checks in configure and make loop forever); do it for all arches.
4. Ship the tree: the guest keeps `/boot/home/tree.commit`. First time, `git archive HEAD | ssh tar -x`; afterwards send only `git diff --name-status <old> HEAD` as a tar of changed files plus a removals list (the `prepare-nightly.sh` scheme: HaikuPorter re-parses any recipe newer than its dependency info, so unchanged files must keep their mtimes). Use `tar` over ssh; Haiku has no virtio-9p or virtio-fs, so there is no shared directory.
5. Sources and dependency packages: default is the guest downloading with its own curl and `--get-dependencies` pulling published packages from pkgman. Fallback `RENKUPORTS_PREFETCH=1` (for guests without TLS, or a slow guest network): run a copy of `RenkuOSPorts/renkuos/bootstrap/arm64/fetch-recipe.sh` on the host for the port and its dependencies' recipes, writing `download/<file>` and `download/<file>.uri` into the tree before step 4, so the guest never downloads. This is what the arm64 bootstrap does because its guest has no curl. Dependencies not yet published are built by HaikuPorter in the guest in the same run (`-y --get-dependencies`) and their hpkgs are uploaded with the port, as the Haiku client does.
6. Run `ssh ... haikuporter --config=... -y --no-source-packages --no-git-repo --get-dependencies -j<n> <port>-<version>` with stdout and stderr to the container's stdout. Record the guest's `packages/` listing with mtimes before and after (same "added or rewritten" rule as Worker.cpp, `*-source.hpkg` excluded; if exit 0 and nothing new, take `<port>-<version>-*` and `<port>_*-<version>-*`).
7. Copy those files to `/out` with `ssh tar -c | tar -x` (not scp, to keep mtimes: the client sends `buildDate` from the file's mtime), verify non-empty, then power the guest off cleanly (`shutdown`, wait for QEMU to exit), release the lock, exit 0 or 1.

QEMU invocations (all headless, `-display none -monitor none -qmp unix:/run/qmp.sock,server,nowait -serial file:/cache/logs/<arch>-serial.log -netdev user,id=n0,hostfwd=tcp:127.0.0.1:2222-:22`). `-smp $RENKUPORTS_JOBS -m $RENKUPORTS_GUEST_MB`. Use TCG settings below unless KVM applies. KVM applies when the image host arch equals the target arch (x86_64 image: x86_64, and i386 on a 64-bit KVM host; arm64 image: arm64) and `/dev/kvm` is readable.

| Target | QEMU | Machine and CPU | Firmware | Disk and net | Notes |
|---|---|---|---|---|---|
| x86_64 | `qemu-system-x86_64` | `-machine q35,accel=kvm\|tcg -cpu host\|max` | SeaBIOS (default); the anyboot image boots BIOS and EFI | `-drive file=...,if=none,id=d0 -device virtio-blk-pci,drive=d0 -device virtio-net-pci,netdev=n0` | Haiku has virtio block and net drivers; fall back to `ide-hd` + `e1000`/`rtl8139` if the image lacks them (verify in the first prototype) |
| x86 | `qemu-system-i386` | `-machine pc,accel=kvm\|tcg -cpu host\|pentium3` | SeaBIOS | as above, `-pci` devices | the `x86_gcc2h` hybrid image; 3 GiB address-space limits mean `-m` above 2048 is wasted: cap at 2048 |
| arm64 | `qemu-system-aarch64` | `-M virt,gic-version=3 -cpu max` (KVM: `-cpu host`, `-M virt,gic-version=host,accel=kvm`) | UEFI: `-drive if=pflash,format=raw,readonly=on,file=/usr/share/AAVMF/AAVMF_CODE.fd -drive if=pflash,format=raw,file=<per-overlay vars.fd copied from AAVMF_VARS.fd>`; the Haiku arm64 image has an ESP with `BOOTAA64.EFI` | `-drive file=...,format=raw\|qcow2,if=none,id=d0 -device virtio-blk-device,drive=d0 -device virtio-net-device,netdev=n0` and `-device ramfb -device qemu-xhci -device usb-kbd` | exactly the flags `renkuos/bootstrap/arm64/phase1b-run.sh` runs today (ramfb and an xhci keyboard are what lets Haiku's app server come up headless) |
| riscv64 | `qemu-system-riscv64` | `-M virt -cpu rv64` (no KVM on riscv hosts we target) | OpenSBI is QEMU's built-in default; Haiku's EFI loader needs U-Boot: `-kernel u-boot.bin` (from the haiku/firmware repo `u-boot/`; `u-boot-qemu` Debian package is the fallback) | `-device virtio-blk-device,drive=x0,bus=virtio-mmio-bus.0 -drive file=...,if=none,format=raw,id=x0 -device virtio-net-device,netdev=n0` | Haiku's own guide uses this shape (`-M virt -m 2048 -kernel u-boot.bin` plus virtio-blk-device plus `-device usb-ehci -device usb-tablet -device usb-kbd -device ati-vga`); alternative `-bios haiku_loader.riscv`. See blocker in G: no evidence a riscv64 guest can run HaikuPorter |

qcow2 overlays are fine for all but the riscv64 and arm64 base images if the loader needs raw: if a guest loader cannot read qcow2 it reads the virtio device, which presents raw blocks, so qcow2 is transparent. The ESP and BFS live inside the image regardless.

Expected speed (TCG, QEMU 8.2, 8 vCPUs on an i9-12900K, from the arm64 bootstrap notes): gettext 1 h 51 min, python3.10 2 h 54 min, cmake plus python3.14 plus curl about 3 h 30 min, gcc 11.8 h; small ports (libogg, libvorbis) under 5 minutes; parallel compiles are fastest at 8 vCPUs and serial work gets slower with more vCPUs (cap `RENKUPORTS_JOBS` default at 8 for TCG). KVM is near-native. A same-arch KVM build costs the guest boot (about 1 to 2 minutes) plus the port. The server's 72 hour lease (`Jobs:LeaseHours`) covers the slowest TCG port; the macOS client on Apple Silicon building arm64 gets TCG inside Docker Desktop (no `/dev/kvm` in its VM) and should say so in the UI.

Cross-compilation is not an option: HaikuPorter on Linux handles only the bootstrap set, hence the guest.

## C. Guest image sources

Haiku publishes nightly images, not release images, for the non-x86 targets. Index pages (HTML directory listings) live at `https://download.haiku-os.org/nightly-images/<variant>/` with files on the CDN (`haiku-nightly.cdn.haiku-os.org/<variant>/`). Each revision has the image, `<file>.sha256` and `<file>.minisig`. Observed 2026-11-18, newest revision hrev60431:

| Target | Variant dir | File name pattern | Example |
|---|---|---|---|
| x86_64 | `x86_64` | `haiku-master-hrev<N>-x86_64-anyboot.zip` | `haiku-master-hrev60431-x86_64-anyboot.zip` |
| x86 | `x86_gcc2h` | `haiku-master-hrev<N>-x86_gcc2h-anyboot.zip` | `haiku-master-hrev60431-x86_gcc2h-anyboot.zip` |
| arm64 | `arm64` | `haiku-master-hrev<N>-arm64-mmc.zip` | `haiku-master-hrev60431-arm64-mmc.zip` |
| riscv64 | `riscv64` | `haiku-master-hrev<N>-riscv64-mmc.zip` | `haiku-master-hrev60431-riscv64-mmc.zip` |

Stable release images are linked from haiku-os.org/get-haiku and exist only for x86_64 and x86 (`x86_gcc2h`); there is no arm64 or riscv64 release. The nightly download page labels arm64 and riscv64 "unsupported, development and testing only". The arm64 and riscv64 images are `mmc` images (MBR, ESP plus BFS), the x86 ones `anyboot`. Nightlies are pruned from the index, so nothing may depend on an `hrev` still being there.

What "x86" means: the client treats the platform string `x86` as HaikuPorter's `x86_gcc2` (`Worker.cpp`: `platform == "x86" ? "x86_gcc2" : platform`), and ports in the test fixtures list `ARCHITECTURES="all !x86_gcc2"` and `SECONDARY_ARCHITECTURES="x86_gcc2 x86"`. So the x86 guest is the gcc2h hybrid image (primary `x86_gcc2`). The repo does not say whether RenkuOS wants the non-hybrid `x86` (gcc 13) as well; open question 5.

Important: a vanilla Haiku nightly is not a builder. Only x86_64 and x86 nightlies carry a package manager with the HaikuPorts repository, so for those a provisioning script can `pkgman install haikuporter gcc ...`. For arm64 and riscv64 there is no HaikuPorts repository; RenkuOS already works around that with its own bootstrap (`renkuos/bootstrap/arm64`) and the Parallels `build-vm` disk (made from a RenkuOS ISO). Therefore all four guests are "RenkuOS builder images" produced by a RenkuOS CI job (a script derived from `make-disk.sh` for arm64, and a pkgman-based provisioning script for x86_64 and x86), compressed to `.qcow2.xz`, and published as release assets. The Haiku nightly URLs above are the inputs to that CI job, not what the container downloads. Decision 2 (pinned URL, SHA-256, cache) applies to the builder images.

Manifest, baked into the image at `/usr/share/renkuports/images.json` and overridable by `RENKUPORTS_IMAGES_MANIFEST`:

    { "version": 1, "images": {
      "arm64": { "url": "https://.../renkuports-guest-arm64-<tag>.qcow2.xz", "sha256": "<of the .xz>", "diskSha256": "<of the decompressed qcow2>", "bytes": 0, "haikuRev": "hrev60431" },
      "x86_64": {...}, "x86": {...}, "riscv64": {...} } }

Pinning and checking: `fetch-images` downloads to `/cache/images/<arch>/disk.qcow2.xz.part` with `curl -fL --retry 3 -C -` (resumable), checks `sha256` before decompressing and `diskSha256` after, then renames atomically and makes the file read-only. A mismatch deletes the file and exits 5. Changing a pinned image means a new manifest (so a new image tag); the work overlay records the base's `diskSha256` (`qemu-img` backing file name includes the first 12 characters) and is discarded when the base changes. The guest image is only trusted for what it builds: servers verify every build twice on different clients.

## D. Repository layout and commands

    containers/builder/Dockerfile          one Dockerfile, two platforms
    containers/builder/entrypoint/         renkuports-build (bash), qemu-<arch>.sh, images.json, guest_ed25519(.pub)
    containers/builder/tests/              bats tests and mock-mode smoke test
    containers/guest-images/               CI scripts that produce the builder images (outside this spec's critical path)
    clients/macos/                         SwiftPM package: Sources/BuilderCore (settings, API, worker, no UI), Sources/RenkuPortsBuilder (SwiftUI app), Tests/BuilderCoreTests, Scripts/make-app.sh
    clients/linux/                         meson project: src/*.c (GTK4 UI, settings, api, worker), data/*.desktop, tests/
    docs/linux-mac-build-clients.md        this file

Guest key: one ed25519 pair is baked into the container and the builder images; the guest only listens on the container's loopback (user-mode network forward bound to 127.0.0.1), so the key is not a credential for anything else. Alternative if that is not acceptable: inject a per-run key through a second small virtio disk read by a guest boot script.

Commands:

| Part | Build | Test |
|---|---|---|
| Container | `docker buildx build --platform linux/amd64,linux/arm64 -t renkuports-builder:dev containers/builder` (single platform with `--load --platform linux/$(uname -m ...)`) | `containers/builder/tests/run.sh` (bats on the host for entrypoint pieces; `docker run ... mock` smoke test) |
| macOS | `cd clients/macos && swift build -c release && Scripts/make-app.sh` (wraps the binary in `RenkuPorts Builder.app`) | `swift test` (BuilderCore only, no Docker, no UI) |
| Linux | `cd clients/linux && meson setup build && meson compile -C build` | `meson test -C build` |

Linux client stack: GTK4 with plain C, `libsoup-3.0` for HTTP (its `SoupMultipart` and streaming bodies handle the upload, and its async API fits the GLib main loop), `json-glib` for JSON, `GSubprocess` for docker and `GChecksum` for SHA-256, so the dependency list is GTK4, libsoup3 and json-glib only. C was chosen over gtkmm so the app has no C++ ABI or binding dependencies and builds on any distro that ships GTK4. Packages: `libgtk-4-dev libsoup-3.0-dev libjson-glib-dev meson`.

macOS client stack: SwiftUI, `URLSession` (uploads via `upload(for:fromFile:)` with the multipart body written to a temp file, since `sha256` and `buildDate` must precede the file part), CryptoKit `SHA256`, `Foundation.Process`. Not App-Sandboxed (it must run `docker`).

## E. Client behaviour versus the Haiku client

Everything else (tabs, Start/Pause/Stop semantics, polling every `poll_seconds` while idle, queue line from `/api/client/me` and `/api/client/jobs?status=Queued&pageSize=1&platform=`, token refresh at 11 h and on 401 with one retry, upload as multipart with `sha256` and `buildDate` first, complete with the last 5 MB of log, fail with summary and log, results list, `run_on_start`, `build_command` test hook, `poll_seconds` minimum 5, `Test connection`) is the same. "Pause" lets the current container finish. The UI has the same Settings and Status tabs.

Settings (key=value file, mode 0600, same key names where they apply; macOS `~/Library/Application Support/RenkuPortsBuilder/settings`, Linux `${XDG_CONFIG_HOME:-~/.config}/renkuports-builder/settings`):

| Key | Default | Notes |
|---|---|---|
| `server`, `client_id`, `secret` | as Haiku | |
| `jobs` | `4` | passed as `RENKUPORTS_JOBS` |
| `work_dir` | macOS `~/Library/Application Support/RenkuPortsBuilder/work`, Linux `~/.local/share/renkuports-builder/work` | per-job `job-<id>/{out,build.log}`, same layout. On macOS it must be under a path Docker Desktop shares (home is shared by default) |
| `run_on_start`, `build_command`, `poll_seconds` | as Haiku | `build_command` replaces the container (contract `<cmd> <port> <version> <outdir>` plus the three env vars); no pre-flight, no container |
| `docker_path` (replaces `haikuporter`) | `docker`; the app also searches `/usr/local/bin`, `/opt/homebrew/bin`, `/Applications/Docker.app/Contents/Resources/bin`, `~/.orbstack/bin` | GUI apps do not inherit the shell PATH |
| `image` (new) | `<registry>/renkuports-builder:<version>` (the app version; manifest list picks the arch) | |
| `kvm` (new) | `auto` | `auto` adds `--device /dev/kvm` on Linux when `/dev/kvm` exists and is readable, never on macOS; `on` always adds it (pre-flight fails if absent); `off` never. Mapped to `RENKUPORTS_ACCEL` |
| `cache_volume` (new) | `renkuports-cache` | |
| `guest_mb` (new) | `8192` | |
| `pull_policy` (new) | `if-missing` | `always` runs `docker pull` before each job |
| `tree_path`, `haikuporter_config` | removed | the container owns the tree and config; `portsRepo` comes from `/api/client/me` as in the Haiku client. If the server has no `portsRepo` the job fails with a clear message (the container cannot clone) |

Job flow in the clients (replaces steps 2 to 4 of the Haiku README):

1. `jobs/next`, create `work_dir/job-<id>/out`, open `build.log`.
2. Pre-flight, once per Start and again when settings change (and shown in Settings as a checklist): (a) `docker version --format '{{.Server.Arch}}'` works (Docker present and daemon running); (b) `docker image inspect <image>`, else `docker pull`; (c) `docker run --rm <image> info --json`: image arch matches the daemon's, the job's platform is in `targets`, the guest image for that arch is `cached` (otherwise run `fetch-images <arch>` first, streamed to the log; a failure is exit 5); (d) `docker info --format '{{.MemTotal}}'` is at least `guest_mb` plus 1 GiB (Docker Desktop VMs default to 2 to 8 GiB, so warn with "raise Memory in Docker Desktop"); (e) the volume name is not used by another running `renkuports-job-*` container. Failures before a job is claimed pause the client with the message; failures after a claim fail the job.
3. Run `docker run` as in section A with `--name renkuports-job-<id>`, stdout and stderr merged into the log and the window, exactly as `_Run` does for HaikuPorter.
4. Exit code mapping per the table in section A. Package collection is `*.hpkg` in `out/` (no `packages/` snapshot diff: the container already did it); skip `-source.hpkg` defensively.
5. Upload, complete, or fail as in the Haiku client. Remove `out/` after, keep `build.log`.

Stop: `docker stop -t 90 renkuports-job-<id>` (macOS: `Process`; Linux: `GSubprocess`), then `docker kill` if it is still running after 100 s; then fail the job with `client stopped`, as the Haiku client does. Quitting the window during a build does the same. If the app crashes and a container is left running, startup lists `docker ps --filter name=renkuports-job-` and offers to stop it (the lock would otherwise block the next job with exit 4).

Log streaming: the client reads the container's stdout and stderr through pipes (no `docker logs`), flushes to the window every 300 ms or 32 KB like `Worker::_Flush`, and keeps the same 5 MB tail for the server.

Status tab additions: the build line shows `arch`, accelerator (`KVM` or `TCG (slow)`, from `info`), the image tag, and "guest booting" until the first guest output.

Desktop integration: none beyond Start-on-launch (`run_on_start`); macOS login item and Linux autostart `.desktop` file are optional later.

## F. Test strategy (no real guest)

Container:

- `mock <arch> <port> <version>` runs the full entrypoint path (arg validation, lock, `info`, log prefixes, signal trap, `/out`, exit codes) but replaces QEMU and ssh with a stub: it behaves as `client/scripts/mock-build.sh` (writes `/out/<port>-<version>-1-<arch>.hpkg`, fails when the port starts with `fail` or equals `RENKUPORTS_MOCK_FAIL`, honours `MOCK_TIMESTAMP` and `MOCK_VARIANT`) and takes `RENKUPORTS_MOCK_SLEEP=<s>` to hang so `docker stop` is testable. Extra mock outcomes: port `infra...` exits 3, port `badimage...` exits 5.
- bats tests: argument validation (exit 2), lock contention (exit 4), SIGTERM within the grace period (exit 75, within `RENKUPORTS_GRACE`), manifest parsing and SHA mismatch (exit 5, using a local `file://` image and a corrupted checksum), the QEMU command line builder per target (`qemu-<arch>.sh --print` outputs the argv; assert against golden files for KVM and TCG).
- One manual or nightly CI job per target with a real guest; not part of the unit tests.

Clients:

- `build_command=` support, as in the Haiku client, with `client/scripts/mock-build.sh`; run the whole claim, upload, complete path against a local server (`docker compose up`), as the Haiku README describes.
- A second hook: `docker_path=<fake docker script>` for a fake `docker` that prints `info --json`, the long running `run`, and honours `stop`; this exercises the pre-flight, exit-code mapping and Stop path without Docker. Alternatively the real `docker` with the image's `mock` subcommand (`image` setting pointing at a locally built image, `renkuports-builder:dev mock` triggered by an `RENKUPORTS_SUBCOMMAND=mock` env the client forwards only when `build_command=mock:`; keep this out of normal settings).
- Unit tests of the non-UI core: settings round trip and 0600 mode, JSON string escaping for logs (invalid UTF-8 becomes `?`, as `JsonString`), multipart body layout (`sha256`, `buildDate`, then `file`), token refresh and 401 retry, exit-code to outcome mapping, `docker run` argument builder (golden), hpkg collection (excludes `-source.hpkg`). Swift: `swift test` with a `URLProtocol` stub. C: GLib test framework (`g_test_*`) with a `SoupServer` stub.

## G. Open questions and blockers (ranked)

1. Blocker, riscv64: there is no evidence a Haiku riscv64 guest can run HaikuPorter at all. Haiku's own guide says of the port "Only the bootloader currently runs"; forum reports show timer bug #17380 and random loader crashes, and the guest would need a native gcc, python and sshd for riscv64, which no repository provides (RenkuOS has an arm64 bootstrap only). Until someone produces a bootable riscv64 builder image that reaches a shell with sshd, the `riscv64` target in the container must exit 3 with a clear message (`info` reports it as unsupported and the clients refuse riscv64 jobs in pre-flight). Decide: bootstrap riscv64 first (large), or ship three targets.
2. Blocker, the guest images do not exist yet: all four builder images (section C) must be produced and published by RenkuOS before the container is useful. arm64 can start from the Parallels `build-vm` disk recipe (needs a `qcow2` conversion and a published location); x86_64 and x86 need a provisioning script; none of the registry or release locations are decided (`<registry>`, image asset host). The Haiku nightly URLs are inputs only, and pinning to an `hrev` in the manifest is only safe if the CI copies the base image somewhere stable.
3. Server has no way to give a job back. `fail` is terminal (`Failed`, admins emailed, an admin must requeue), so every infrastructure failure of a client (Docker down, image missing, QEMU crash, user pressed Stop) currently burns the job. Options: add `POST /api/client/jobs/{id}/release` returning the job to Queued without a failed build (preferred; small server change plus tests), or leave the claim to expire after the 72 h lease. Until decided, clients use `fail` with prefix `infrastructure:`.
4. Performance of arm64 on Apple Silicon: Docker Desktop's Linux VM exposes no `/dev/kvm`, so macOS arm64 builds run under TCG (hours for gcc, 4 hours of recipe parsing with a cold overlay). Native HVF acceleration would need QEMU on macOS outside the container, which contradicts decision 1. Decide whether that is acceptable or whether the macOS client later gets a no-container QEMU mode. Also confirm nested KVM on Apple M3 or later through newer Docker Desktop or Apple Virtualization, which is not assumed here.
5. Which x86 flavour: this spec uses the `x86_gcc2h` hybrid image with `x86` meaning `x86_gcc2` (per `Worker.cpp`). If RenkuOS also wants the non-hybrid 32-bit `x86` (gcc 13) platform, it needs a separate guest and a distinct platform string; the repo does not say.
6. Guest virtio and boot details unverified on current nightlies: whether x86_64 and x86 anyboot images and the arm64 mmc image boot cleanly with the device sets in the table (arm64 is proven with the existing `phase1b-run.sh` flags; x86 is expected, not tested here). First prototype task: boot each image under the container's QEMU and fall back to `ide-hd` and `e1000` where needed.
7. Persistent work overlay versus clean guest per job: a persistent overlay (default here) makes later jobs fast but means the build environment (installed packages, dependency cache) is not identical between two verification builds on different clients. Verification compares contents, so a polluted guest shows up as a mismatch, not a silent error; a mismatch then triggers `RENKUPORTS_FRESH=1` in the retry. Confirm that this is acceptable.
8. Disk and memory footprint: images are tens of GB when expanded, a guest needs 8 to 12 GiB RAM, and the container needs that plus 1 GiB. Docker Desktop on Mac defaults are too small; the pre-flight warns, nothing more. Confirm minimum supported host specs (suggest 16 GiB RAM, 100 GiB disk).
9. Secret storage: the Haiku client keeps the secret in a 0600 file. macOS Keychain and Linux libsecret would be better but are not required for parity; decide whether to add them.
10. Registry and licensing for the container image (QEMU is GPL, the image bundles Debian packages and the fixed guest key); where the image is published (GHCR is assumed) and whether `release.yml`, which today explicitly does not publish container images, publishes this one.
