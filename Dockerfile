# API image: the ASP.NET Core server on Kestrel (port 8080, non-root), plus Haiku's package and package_repo host
# tools for the pkgman repository index. The SPA is served by the nginx image (nginx/Dockerfile), not from here.

# Haiku host tools, built from source for the image's own architecture (amd64 for App Service, arm64 on this Mac).
# Ubuntu 24.04, like the aspnet:10.0 runtime, so the tools find the same libstdc++, zlib and zstd there.
FROM ubuntu:24.04 AS haiku-tools
ARG HAIKU_REPO=https://github.com/RenkuOS/Source.git
ARG HAIKU_COMMIT=b2dd07cd0374784547b2f4bfd33d95ed541ae983
ARG BUILDTOOLS_COMMIT=33d46bf5c300bd34cd07c596431e4545c754ac03
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git g++ make bison flex python3 nasm attr \
        zlib1g-dev libzstd-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /b
RUN git init -q buildtools && cd buildtools && git fetch -q --depth 1 https://github.com/haiku/buildtools.git "$BUILDTOOLS_COMMIT" \
    && git checkout -q FETCH_HEAD && cd jam && make && ./jam0 install
RUN git init -q haiku && cd haiku && git fetch -q --depth 1 "$HAIKU_REPO" "$HAIKU_COMMIT" && git checkout -q FETCH_HEAD
RUN mkdir haiku/generated && cd haiku/generated && ../configure --host-only \
    && jam -q -j"$(nproc)" '<build>package_repo' '<build>package' \
    && mkdir /out && cp objects/linux/*/release/tools/package/package objects/linux/*/release/tools/package_repo/package_repo \
        objects/linux/lib/libroot_build.so objects/linux/lib/libbe_build.so objects/linux/lib/libpackage_build.so /out/ \
    && LD_LIBRARY_PATH=/out /out/package_repo help > /dev/null

# SDK plus the tools, for the test that runs package_repo on real packages (tests/run-haiku-tools-test.sh).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tools-test
COPY --from=haiku-tools /out/ /opt/haiku-tools/
# `package create` checks licenses against the build tree's data directory (compiled in as /b/haiku/generated/../data/system/data).
RUN mkdir -p /b/haiku/generated/attributes /b/haiku/data/system/data/licenses && echo MIT > /b/haiku/data/system/data/licenses/MIT
ENV RENKUPORTS_REQUIRE_HAIKU_TOOLS=1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RenkuPorts.Server/RenkuPorts.Server.csproj src/RenkuPorts.Server/
RUN dotnet restore src/RenkuPorts.Server/RenkuPorts.Server.csproj
COPY src/RenkuPorts.Server/ src/RenkuPorts.Server/
# The build version (GET /api/version): VERSION=$(scripts/version.sh) docker compose build. After the restore, so it does not bust that layer.
ARG VERSION=0.0.0-dev
RUN dotnet publish src/RenkuPorts.Server/RenkuPorts.Server.csproj -c Release -o /app --no-restore -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git: recipe dates come from the ports tree's history (POST /api/recipes/sync).
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=haiku-tools /out/ /opt/haiku-tools/
# `package extract` (build verification) stores file attributes under this path, compiled into libroot_build; without it every
# extraction fails and packages are compared byte for byte, which never matches. The verifier empties it after each job.
RUN mkdir -p /b/haiku/generated/attributes && chown -R "$APP_UID" /b/haiku/generated
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RenkuPorts.Server.dll"]
