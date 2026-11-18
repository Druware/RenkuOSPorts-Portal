# arm64 builder image

Builds the arm64 guest image that `containers/builder` boots under QEMU (see "Builder image requirements" in `containers/builder/README.md`), from a RenkuOS source checkout rather than a Haiku nightly.

    SRC=../RenkuOS/Source ./build.sh   # all phases: setup jam assemble provision package
    ./build.sh jam assemble       # or some of them, in order

Outputs land in `out/` (git-ignored): `renkuports-guest-arm64-<describe>.qcow2.xz`, its `.sha256`, `images-entry.json` (the `arm64` manifest entry with an empty `url`, plus the RenkuOS commit) and `provision-serial.log`. Variables and defaults are at the top of `build.sh`.

## Phases

| Phase | Runs in | Does |
|---|---|---|
| `setup` | cross-compiler image | copies `SEED_VOL`'s built tree into `VOL` once (`cp -a`, so mtimes survive and jam rebuilds only what changed), checks out `REF` of `SRC` there, installs `UserBuildConfig` and `files/`; then makes ssh host keys in the builder image |
| `jam` | cross-compiler image | `jam -q -j$JOBS @nightly-raw` (a sparse 40 GiB BFS image with the packages in `UserBuildConfig`, fetched from the RenkuOS arm64 repository the tree names) and `haiku_loader.efi` |
| `assemble` | cross-compiler image | the arm64 mmc layout (MBR, ESP with `EFI/BOOT/BOOTAA64.EFI` at 4 MiB, BFS at 36 MiB) written sparse into `/w/disk.raw` |
| `provision` | builder image | converts to qcow2 and boots it once with the entrypoint's own `qemu-argv arm64 tcg` and a seed disk holding a throwaway key; runs `check-guest.sh` over ssh, waits for first-boot processing to finish, replaces the image's pkgman repositories (a HaikuPorts arm64 URL that does not exist and the RenkuOS system repository) with `REPO_URL`, deletes the key and shuts down |
| `package` | builder image | compacts the qcow2, `xz -6`, SHA-256 of both, `images-entry.json` |

## What the image adds to a nightly build

- Packages: HaikuPorter, Python 3.14, gcc, make, cmake, meson, autotools, git, curl, wget, coreutils, openssh and the rest listed in `UserBuildConfig`, all from the arm64 repository in `build/jam/repositories/HaikuPorts/arm64`; plus `haiku_devel` and `makefile_engine` from the same build, which arm64's `DevelopmentMin` leaves out.
- `/system/settings/launch/renkuseed` runs `/system/non-packaged/bin/renkuseed` (`files/renkuseed.sh`) on every boot: it mounts the `RENKUSEED` volume read-only, installs its `authorized_keys` as `~/config/settings/ssh/authorized_keys` (directory 700, file 600), unmounts it, and logs to the serial console.
- The `sshd` user and group, and ssh host keys made at build time: net_server starts sshd before first-boot processing would create either, and without them sshd exits on the first boot. Every copy of a published image has the same host keys; the entrypoint does not check them, and the guest is only reachable on the container's loopback.
- An empty `~/config/settings/Locale settings`, so the first boot does not stop at FirstBootPrompt.

The toolchain image `renkuos-cross-compiler:arm64-master-native` is built from `build-arm64/toolchain-ctx` (`Dockerfile`, `build-toolchain.sh`) in the RenkuOS working area. Without a seed volume, `setup` clones `SRC` and runs `configure --cross-tools-prefix`; that path has not been tried.
