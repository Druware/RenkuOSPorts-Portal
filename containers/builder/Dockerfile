# RenkuPorts builder: boots a RenkuOS guest under QEMU and runs HaikuPorter in it. Built per platform:
#   docker buildx build --platform linux/arm64 --build-arg VERSION=$(scripts/version.sh) -t renkuports-builder:<ver>-arm64 --load containers/builder
# Bump the digest by a deliberate commit only.
FROM debian:trixie-slim@sha256:a72b4433de1cf7d6bae0c4cee74d0547f58601a643b3375f070c51600d63b6b2

# riscv64 is disabled (see README); enabling it needs qemu-system-misc and u-boot-qemu here.
RUN apt-get update && apt-get install -y --no-install-recommends \
        qemu-system-arm qemu-system-x86 qemu-utils qemu-efi-aarch64 \
        openssh-client git curl ca-certificates xz-utils jq socat dosfstools mtools \
    && rm -rf /var/lib/apt/lists/*

ARG VERSION=0.0.0-dev
COPY entrypoint/renkuports-build /usr/local/bin/renkuports-build
COPY entrypoint/images.json /usr/share/renkuports/
RUN chmod 755 /usr/local/bin/renkuports-build \
    && echo "$VERSION" > /usr/share/renkuports/version && mkdir -p /cache /out

ENTRYPOINT ["/usr/local/bin/renkuports-build"]
