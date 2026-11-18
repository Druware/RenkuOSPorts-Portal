# x86_64 builder image

The guest disk that `containers/builder` boots for `build x86_64 ...`: RenkuOS built from source with jam (`@nightly-raw`, 40 GiB sparse BFS), plus HaikuPorter and its build tools, a boot-time seed-key installer, and one provisioning boot. It meets "Builder image requirements" in `../../builder/README.md`.

## Files

| File | Does |
|---|---|
| `build-image.sh` | host driver: steps `env`, `jam`, `provision`, `pack` (no argument: all four) |
| `Dockerfile` | build environment: RenkuOS's cross-compiler image plus `qemu-utils` |
| `jam-build.sh` | in the build environment: checks out `RENKUOS_REF` into the work volume, copies `UserBuildConfig` into `generated.x86_64`, configures as CI does, `jam -q @nightly-raw`, converts the raw image to `out/x86_64/disk-raw.qcow2` |
| `UserBuildConfig` | 40960 MiB sparse image; HaikuPorter, python3.14, git, gcc, binutils, make, autotools, patch, coreutils and other build tools as activated system packages (all from the pinned `build/jam/repositories/HaikuPorts/x86_64`); `haiku_devel` and `makefile_engine`; the x86_64 `virtio_block` driver; the seed-key installer |
| `install-seed-key`, `launch/renkuports-seed` | the boot script and the launch_daemon job (in `/boot/system/settings/launch`) that runs it at every boot: mounts the FAT volume `RENKUSEED` read-only with `mountvolume`, copies `authorized_keys` to `~/config/settings/ssh/authorized_keys` (directory 700, file 600), unmounts, logs to `/var/log/renkuports-seed.log` |
| `provision.sh` | in the builder container: boots a copy of the raw disk with the container's own `qemu-argv x86_64 tcg` and a throwaway seed key until ssh answers (ACPI power-off and a second boot after `BOOT_WAIT`, default 600 s), drops every pkgman repository but HaikuPorts (`pkgman drop-repo --yes`), `pkgman install libtool`, checks the commands the entrypoint uses, removes the throwaway key, powers off |
| `pack.sh` | in the builder container: compacts the disk, `xz -9`, writes `images-entry.json` (the manifest entry, empty `url`) and `images-local.json` (a `file:///images/...` manifest for testing) |

Outputs go to `../out/x86_64/` (gitignored). The jam tree and its downloads stay in the Docker volume `renkuports-builder-image-x86_64`, so a rebuild is incremental.

Why a provisioning boot: openssh's host keys are made by its post-install script on the first boot, after net_server has already tried to start `sshd -D`, so sshd only listens from the second boot on. Shipping an image that has booted once avoids that on every user's first job. It also installs libtool, the one build tool jam cannot add: its dependency `libtool_libltdl` is missing from the pinned package list. libtool then comes from the live HaikuPorts repository (2.6.2-1 at the time of writing), so that one package is not pinned.

The provisioned guest knows one package repository, HaikuPorts x86_64 (`eu.hpkg.haiku-os.org`). The image's Renku system repository (`renkuos.freemyip.com`) is dropped, since `--get-dependencies` could otherwise replace the system packages built here, and so are the third-party repositories from `data/settings/package-repositories` (Spritz Depot). There is no RenkuOSPorts x86_64 repository yet. `pkgman drop-repo -y` is broken in this pkgman; `--yes` works.

Things found while making it work, all handled here or in the container:

- RenkuOS's `build/jam/packages/Haiku` ships `virtio_block` only on arm, arm64 and riscv64, so the x86_64 kernel saw no virtio disk (still so at a35b4b4). `UserBuildConfig` adds the x86_64 build of it under `home/config/non-packaged/add-ons/kernel` with a boot-module link. It cannot go under `system/non-packaged`: the boot loader opens `system/...` paths inside the haiku package.
- RenkuOS x86_64 fails every I/O through the legacy (transitional) virtio-pci interface, which QEMU uses by default for devices on q35's root bus. The container's x86_64 argv therefore passes `disable-legacy=on` to its virtio devices (`../../builder/entrypoint/renkuports-build`). Rechecked at a35b4b4: a transitional `virtio-blk-pci` still fails every read.
- Once, with modern virtio, a boot panicked in the slab allocator on BFS's first write (`object_cache_alloc` from `cache_add_transaction_listener`, called by `mount_server`). It did not happen again in the later boots. Watch for it.
- Right after a pkgman transaction, `shutdown` over ssh returned without powering off; ACPI power-off (QMP `system_powerdown`) worked. The entrypoint already falls back to it after 20 s.
- The seed volume mounts as `/renkuseed` (the FAT driver lowercases the label); `install-seed-key` tries both spellings.

## Rebuild

Prerequisites: Docker, a RenkuOS/Source checkout, the builder container image (`../../builder/README.md`, tagged `renkuports-builder:dev` here), and the cross-compiler image `renkuos-cross-compiler:x86_64-master-native`. To build the latter (on an arm64 Mac it is a native arm64 toolchain targeting x86_64, much faster than an amd64 container under emulation):

    docker build -t renkuos-cross-compiler:x86_64-master-native --build-arg ARCHITECTURE=x86_64 \
      --build-arg HAIKU_REPO=https://github.com/RenkuOS/Source.git --build-arg HAIKU_REV=main \
      <RenkuOS/Source>/3rdparty/docker/cross-compiler

Then:

    RENKUOS_SRC=<RenkuOS/Source> containers/builder-images/x86_64/build-image.sh

`RENKUOS_REF` is any ref or commit of that checkout. It defaults to `refs/remotes/origin/main`, because the checkout's local `main` can lag far behind (it was 215 commits behind when this image was made); `out/x86_64/build-info` records the commit and revision actually built. the checkout itself is mounted read-only and never changed. Other knobs: `BUILDER_IMAGE` (default `renkuports-builder:dev`), `WORK_VOLUME`, `OUT`, `RENKUPORTS_GUEST_MB` for the provisioning boot (default 4096). Steps can run singly, e.g. `build-image.sh provision pack` to redo the boot from the existing `disk-raw.qcow2`; the boot script and `UserBuildConfig` are baked in by `jam`, so after changing them run `build-image.sh jam provision pack`.

## Test with the builder container

    docker run --rm --init -v renkuports-test-cache:/cache -v "$PWD/containers/builder-images/out/x86_64:/images:ro" -v /tmp/out:/out \
      -e RENKUPORTS_IMAGES_MANIFEST=/images/images-local.json -e RENKUPORTS_GUEST_MB=4096 \
      -e RENKUPORTS_PORTS_URL=https://github.com/haikuports/haikuports -e RENKUPORTS_PORTS_REF=master \
      renkuports-builder:dev build x86_64 libyaml 0.2.5

## Publishing

Upload `renkuports-guest-x86_64-<rev>-<commit>.qcow2.xz` as a GitHub release asset, put its URL into `images-entry.json` and that entry under `images.x86_64` in `containers/builder/entrypoint/images.json`. GitHub limits one asset to 2 GiB. The hrev60072+1053 asset is 491 MiB (515368828 bytes; the qcow2 is 507 MiB on disk, 40 GiB virtual), well under that.
