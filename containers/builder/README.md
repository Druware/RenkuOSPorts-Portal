# RenkuPorts builder container

Builds one port for a RenkuOS platform by booting a RenkuOS builder guest under QEMU and running HaikuPorter in it over ssh. The Linux and macOS build clients run it; the full design is `docs/linux-mac-build-clients.md`.

## Status

- Guest builder images are not published yet. `images.json` has placeholder entries, so `build` and `fetch-images` exit 5 ("no builder image published for <arch>") until real URLs and SHA-256 values are filled in. The x86_64 image is built by `../builder-images/x86_64` and has been booted with this container through a `file://` manifest; the other arches have no image yet.
- riscv64 is unsupported: `build riscv64 ...` exits 3 and `info --json` lists it under `unsupported`. Its QEMU arguments are kept as a comment in `qemu_argv` in `entrypoint/renkuports-build`; enabling it also needs `qemu-system-misc` and `u-boot-qemu` in the Dockerfile.

## Build

One Dockerfile, one image per host architecture (the base image is pinned by digest; bump it in a deliberate commit):

    ver=$(scripts/version.sh)
    docker buildx build --platform linux/arm64 --build-arg VERSION=$ver -t renkuports-builder:$ver-arm64 --load containers/builder
    docker buildx build --platform linux/amd64 --build-arg VERSION=$ver -t renkuports-builder:$ver-amd64 --load containers/builder

For a registry, push both tags and join them as the manifest list `renkuports-builder:$ver` (`docker buildx imagetools create -t <registry>/renkuports-builder:$ver <registry>/renkuports-builder:$ver-amd64 <registry>/renkuports-builder:$ver-arm64`).

## Run

    docker run --rm --init --name renkuports-job-<jobId> [--device /dev/kvm] \
      -v renkuports-cache:/cache -v <outdir>:/out \
      -e RENKUPORTS_PORTS_URL=<git url> -e RENKUPORTS_PORTS_REF=<branch> -e RENKUPORTS_JOBS=4 -e RENKUPORTS_GUEST_MB=8192 \
      renkuports-builder:<ver>-<hostarch> build <arch> <port> <version>

| Subcommand | Does |
|---|---|
| `build <arch> <port> <version>` | the job: verified guest image, qcow2 work overlay, ports tree update, QEMU boot, HaikuPorter over ssh, new `*.hpkg` copied to `/out` |
| `mock <arch> <port> <version>` | same contract without QEMU: writes the same file as `client/scripts/mock-build.sh`; ports starting with `fail` (or equal to `RENKUPORTS_MOCK_FAIL`) exit 1, `infra...` exit 3, `badimage...` exit 5; `RENKUPORTS_MOCK_SLEEP=<s>` hangs first |
| `info [--json]` | version, image arch, targets, accelerator and guest image state per target; boots nothing |
| `fetch-images [<arch>...]` | downloads and verifies guest images into `/cache/images` ("Prepare") |
| `gc` | removes work overlays and images the manifest no longer names |
| `qemu-argv <arch> <kvm\|tcg>` | testing aid: prints the QEMU command line, one argument per line |

Environment variables, mounts and the per-job sequence are in sections A and B of the design. `RENKUPORTS_REPO_URL`, when set, must match `^https?://[A-Za-z0-9._~:/?#@!$&()*+,;=%-]+$` (exit 2 otherwise) and is added in the guest with `pkgman add-repo` unless `pkgman list-repos` already lists it.

The first `build` on a cache volume generates an ed25519 key pair at `/cache/ssh/id_ed25519` (mode 600); it stays with the volume, so two volumes have different keys. On every boot the entrypoint builds a small read-only FAT seed disk labelled `RENKUSEED` holding that public key as `authorized_keys` and attaches it as the second virtio disk.

## Builder image requirements

A guest image in the manifest must:

- run sshd on port 22 with key login for user `user`;
- run a boot script that copies `authorized_keys` from the `RENKUSEED` FAT volume to `~/config/settings/ssh/authorized_keys` before or while sshd starts (the entrypoint retries ssh until the boot timeout, so a late copy is fine);
- provide `haikuporter`, `pkgman`, `tar`, `date -s`, `stat -c` and `shutdown`, as section B of the design lists.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | built; at least one `*.hpkg` in `/out` |
| 1 | build failed, or HaikuPorter succeeded without a package for the port |
| 2 | bad arguments or environment |
| 3 | infrastructure failure (riscv64, KVM requested but absent, clone failed, guest did not boot, ssh lost, QEMU crash); the last 100 serial log lines are appended once QEMU has started |
| 4 | the cache volume is busy (another container holds `/cache/build.lock`) |
| 5 | guest image missing, download failed or SHA-256 mismatch |
| 75 | stopped by SIGTERM/SIGINT after shutting the guest down (ssh `shutdown`, QMP `system_powerdown`, QMP `quit` after `RENKUPORTS_GRACE` seconds; arm64 skips `system_powerdown` and quits once the wait after ssh `shutdown` (20 s, or the grace if shorter) has passed, because RenkuOS arm64 does not handle the ACPI power button) |

## Tests

    containers/builder/tests/run.sh [image]

Runs on the host against a built image (default `renkuports-builder:dev`): argument validation, `info --json` shape, mock outcomes, the placeholder manifest, SHA-256 checks with a local `file://` image, per-volume ssh keys, lock contention, `docker stop` exiting 75 within the grace (with and without a running QEMU, the latter also checking the seed disk), and the QEMU argv against `tests/golden/` for KVM and TCG (plus a check that QEMU accepts the TCG argv). Requires Docker and `jq`.
