# Build environment for the x86_64 builder image: RenkuOS's cross-compiler image (Source/3rdparty/docker/cross-compiler,
# ARCHITECTURE=x86_64; native on an arm64 host) plus qemu-img to turn jam's raw image into qcow2. See README.md.
ARG BASE=renkuos-cross-compiler:x86_64-master-native
FROM ${BASE}
RUN apt-get update && apt-get install -y --no-install-recommends qemu-utils && rm -rf /var/lib/apt/lists/*
