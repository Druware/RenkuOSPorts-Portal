# RenkuOSPorts-Public

A public package repository and remote build coordinator for RenkuOSPorts, modelled on the HaikuPorts package server. A web site lists the `.hpkg` packages per platform (`x86`, `x86_64`, `arm64`, `riscv64`) and serves them for download. Signed-in users register build clients; a build client is a command-line program that polls the coordinator for the next queued recipe for its platform, builds it with a configurable script, and uploads the resulting packages. Every upload is recorded in SQL Server (file, platform, size, SHA-256, build date, upload date, recipe date, the user who built it). Package files are stored in blob storage. Failed builds are logged, shown on the admin dashboard and emailed to the administrators. The server is ASP.NET Core on .NET 10 with ASP.NET Identity, the front end is Vue 3 built with Vite, and everything runs locally in Docker Compose with Azure stand-ins. The plan for hosting it on Azure is in [docs/azure-deployment.md](docs/azure-deployment.md). The project is released under the MIT License (see `LICENSE`).

## Architecture

| Component | Tech | Local stand-in | Azure service |
|---|---|---|---|
| Web UI and API (`src/RenkuPorts.Server`, SPA from `web/`) | ASP.NET Core 10, Identity, Vue 3 | `web` container on port 8080 | App Service (Web App for Containers) |
| Database | EF Core, SQL Server | `sql` container (SQL Server 2022, host port 14333) | Azure SQL Database |
| Package and log storage | Azure.Storage.Blobs, container `packages` | `azurite` container (blob, host port 10000) | Storage account |
| Notification email | SMTP or Azure Communication Services | `mailpit` (SMTP 1025, UI 8025) | Azure Communication Services Email |
| Build client (`src/RenkuPorts.Client`) | .NET 10 console app, runs a build script | `dotnet run`, or a launchd agent on this Mac | Runs anywhere that can reach the server over HTTPS |

## Run locally

Requirements: Docker, the .NET 10 SDK, Node 22 (only for web development). The compose file mounts `../RenkuOSPorts` (a sibling directory of this project, read-only) at `/ports`; that is the ports tree the recipe sync reads. Alternatively set `PortsTree__GitUrl` (and optionally `PortsTree__Branch`, default `main`) and the server clones the repository itself into `PortsTree:Path` (blobless clone) and, on later syncs, fetches and hard-resets to the branch tip; this is what the Azure deployment uses. Only one sync can run at a time per server instance; a second request gets 409.

    cp .env.example .env
    docker compose up -d --build

The SQL Server image is amd64 only and runs under Rosetta on Apple silicon, so the first start takes a while. The server applies EF migrations, creates the blob container and seeds the roles and admin account at startup.

| What | URL |
|---|---|
| Web UI and API | http://localhost:8080 |
| Mailpit (captured emails) | http://localhost:8025 |
| SQL Server | `localhost,14333`, user `sa`, password `SA_PASSWORD` from `.env` |
| Azurite blob endpoint | http://localhost:10000 |

Seeded admin: the values of `ADMIN_EMAIL` and `ADMIN_PASSWORD` in `.env` (the defaults in `.env.example` are `admin@renkuos.local` and `RenkuAdmin_Local1!`). GitHub sign-in is optional: set `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` in `.env` (an OAuth app whose callback URL is `http://localhost:8080/signin-github`). Without them the GitHub button is not offered.

For front-end work, `cd web && npm ci && npm run dev` starts Vite and proxies `/api` and `/repo` to http://localhost:8080. `npm run mock` runs a mock API (`web/mock/server.js`) instead.

## First run

1. Sign in at http://localhost:8080 as the seeded admin.
2. On the admin page run the recipe sync (`POST /api/recipes/sync`). It first updates the checkout if `PortsTree:GitUrl` is set, then scans `PortsTree:Path` for `<category>/<port>/<port>-<version>.recipe` and takes each recipe's date from `git log` (file mtime if git is unavailable). Recipes are added or updated; the response gives `added`, `updated`, `total`.
3. Queue a job for a recipe and one or more platforms (`POST /api/jobs`). The version defaults to the highest known one; a platform that already has a Queued or InProgress job for that recipe is skipped.
4. On the Clients page create a build client (name and platform). The secret is shown once and stored only as a hash. Any signed-in user can create clients for their own account; packages a client uploads are attributed to its owner.

## Run the first client on this Mac (arm64)

Foreground, for a first try. Config comes from, in increasing precedence, `appsettings.json` next to the binary, `appsettings.json` in the working directory, `RENKUPORTS_*` environment variables and `--Key value` arguments. `--once` handles at most one poll and exits.

    dotnet run --project src/RenkuPorts.Client -- \
      --Server http://localhost:8080/ --ClientId <id> --Secret <secret> \
      --BuildCommand "$PWD/client/scripts/mock-build.sh" --WorkDir /tmp/renkuports-work --once

Keys: `Server`, `ClientId`, `Secret`, `BuildCommand` are required (the client exits with code 2 if one is missing); `WorkDir` defaults to `work`; `PollSeconds` defaults to 60. Use `mock-build.sh` to test the whole path without a real build. For real arm64 builds point `BuildCommand` at `client/scripts/build-stack-arm64.sh`.

As a background service on this Mac, using the files in `client/`:

    dotnet publish src/RenkuPorts.Client -c Release -r osx-arm64 --self-contained \
      -p:PublishSingleFile=true -o ~/Library/Application\ Support/renkuports-client/bin
    cp client/appsettings.example.json ~/Library/Application\ Support/renkuports-client/appsettings.json
    # edit appsettings.json: ClientId, Secret, and Server if not local
    cp client/org.renkuos.ports-client.plist.example ~/Library/LaunchAgents/org.renkuos.ports-client.plist
    launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/org.renkuos.ports-client.plist

The plist and `appsettings.example.json` contain absolute `/Users/arsatori/...` paths; adjust them for another account. The plist is a LaunchAgent rather than a daemon because Docker Desktop and Parallels run in the user session, and it sets a `PATH` that includes `/usr/local/bin` and `/opt/homebrew/bin` since launchd does not. Output goes to `~/Library/Logs/renkuports-client.log`. Stop it with `launchctl bootout gui/$(id -u)/org.renkuos.ports-client`: the client receives SIGTERM, stops the running build (the build script gets SIGTERM first, so `build-stack-arm64.sh` pauses the stack), and reports the job as failed with "client stopped".

## Build command contract

The client runs `<BuildCommand> <port> <version> <outdir>` with stdout and stderr captured to the job log, and these environment variables set: `RENKUPORTS_PLATFORM`, `RENKUPORTS_JOB_ID`, `RENKUPORTS_RECIPE_PATH`. The script must write the built packages as `*.hpkg` files directly into `<outdir>` and exit 0. A non-zero exit, or exit 0 with no `.hpkg` in `<outdir>`, fails the job. Every `*.hpkg` in `<outdir>` is uploaded: the client computes its SHA-256 and sends a multipart request whose `sha256` and `buildDate` fields (the file's modification time) come before the `file` part, streaming the file without buffering. The server streams the bytes through a SHA-256 hash straight into blob storage, rejects the upload if the claimed hash does not match, and uses no temp file. File names must be plain `*.hpkg` names. The last 5 MB of the log is sent to the server. The per-job directory under `WorkDir` keeps `build.log`; the `out` directory is deleted after the job.

Two scripts ship in `client/scripts/`:

- `mock-build.sh` writes a small fake `<port>-<version>-1-<arch>.hpkg` (arch from `ARCH`, else `RENKUPORTS_PLATFORM`, else `arm64`). It fails for ports whose name starts with `fail` or equals `MOCK_FAIL`. Output is deterministic, so rebuilding uploads an identical file.
- `build-stack-arm64.sh` builds on this Mac's HaikuPorter buildmaster stack (`RenkuOS-on-Parallels/build-stack`, location overridable with `BUILD_STACK`; `WAIT_SECONDS` sets the poll interval while the stack is busy). It resumes the Parallels VM and starts the containers, waits while another buildmaster run is active, runs `buildmaster build <port>-<version>`, prints the build logs, and copies that port's hpkgs to `<outdir>`. It exits 0 only if the port built or its packages already existed.

## Job lifecycle

A job is created `Queued`. A client's `POST /api/client/jobs/next` atomically claims the oldest Queued job for its platform: the job becomes `InProgress`, records the client, claim time and lease expiry (now plus `Jobs:LeaseHours`, default 72), and increments `attempts`. The client uploads each package and then calls complete, which requires at least one uploaded package and sets `Succeeded`; the packages appear in the public repository immediately. If the build or upload fails the client calls fail: the job becomes `Failed`, the claim is cleared, the summary and log are kept, and every user in the `Admin` role gets an email. An admin can requeue a Failed or Cancelled job, or cancel a Queued one. At each `next` call, InProgress jobs whose lease has expired are returned to Queued and the admins are emailed; a late complete or fail from the old client then gets 409 (job no longer InProgress) or 403 (reclaimed by another client). Uploading a package whose platform and file name already exist is a no-op if the SHA-256 matches and a 409 if it differs. Logs are stored in blob storage as `logs/<jobId>.log`, packages as `<platform>/<sha256>/<fileName>`, and the ASP.NET Data Protection keys as `dataprotection/keys.xml` in the same container.

## Tests

The xUnit integration tests run the real server against the compose SQL Server and Azurite, so start those first. The test database is `RenkuPortsTests` and the blob container `packages-test`; the SA password comes from `RENKUPORTS_TEST_SA_PASSWORD`, defaulting to the one in `.env.example`.

    docker compose up -d sql azurite
    dotnet test tests/RenkuPorts.Server.Tests

## Configuration notes

Besides the keys in `appsettings.json`: `Database:MigrateOnStartup` (default true; set false to apply migrations from a pipeline, e.g. `dotnet ef migrations script --idempotent -p src/RenkuPorts.Server -o migrate.sql`), `Blob:ServiceUri` and `Email:AzureCommunication:Endpoint` (use `DefaultAzureCredential` instead of the connection strings, which remain the fallback), `PortsTree:GitUrl` and `PortsTree:Branch`, and `APPLICATIONINSIGHTS_CONNECTION_STRING` (when set, telemetry goes to Application Insights through OpenTelemetry). SQL access uses EF's retry-on-failure; a transient error after the job-claim statement has committed can make the retry claim a second job, and that extra job simply waits out its lease. Behind a TLS-terminating proxy set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.

## Known gaps

- Managed identity access to Blob Storage and Communication Services, and Application Insights, are implemented but not tested against real Azure.
- The recipe-sync lock is per server instance, so two instances could sync at once.
- There is no pkgman-compatible repository index (`repo.info`, `repo`) yet; packages can be listed and downloaded from `/repo/<platform>/packages/<file>`, but Haiku's package manager cannot add the site as a repository.
- Package downloads are proxied through the web app and do not support HTTP range requests.
- Only arm64 has a real build script; `x86`, `x86_64` and `riscv64` clients need their own `BuildCommand`.
