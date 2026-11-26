# RenkuOSPorts-Public

A public package repository and remote build coordinator for RenkuOSPorts, modelled on the HaikuPorts package server. A web site lists the `.hpkg` packages per platform (`x86`, `x86_64`, `arm64`, `riscv64`) and serves them for download. Signed-in users register build clients; a build client is a command-line program that polls the coordinator for the next queued recipe for its platform, builds it with a configurable script, and uploads the resulting packages. Every upload is recorded in SQL Server (file, platform, size, SHA-256, build date, upload date, recipe date, the user who built it). Package files are stored in blob storage. Failed builds are logged, shown on the admin dashboard and emailed to the administrators. The server is ASP.NET Core on .NET 10 with ASP.NET Identity, the front end is Vue 3 built with Vite, and everything runs locally in Docker Compose with Azure stand-ins. The plan for hosting it on Azure is in [docs/azure-deployment.md](docs/azure-deployment.md). The project is released under the MIT License (see `LICENSE`).

## Architecture

| Component | Tech | Local stand-in | Azure service |
|---|---|---|---|
| Web UI (`web/`, `nginx/`) | Vue 3 SPA served by nginx, which reverse-proxies the API | `web` container on port 8080 | App Service (Web App for Containers) |
| API (`src/RenkuPorts.Server`) | ASP.NET Core 10 on Kestrel, Identity | `api` container (port 8080, reached only through `web`) | App Service (Web App for Containers) |
| Database | EF Core, SQL Server | `sql` container (SQL Server 2022, host port 14333) | Azure SQL Database |
| Package and log storage | Azure.Storage.Blobs, container `packages` | `azurite` container (blob, host port 10000) | Storage account |
| Notification email | SMTP or Azure Communication Services | `mailpit` (SMTP 1025, UI 8025) | Azure Communication Services Email |
| Build client (`src/RenkuPorts.Client`) | .NET 10 console app, runs a build script | `dotnet run`, or a launchd agent on this Mac | Runs anywhere that can reach the server over HTTPS |

## Run locally

Requirements: Docker, the .NET 10 SDK, Node 22 (only for web development). The compose file mounts `../RenkuOSPorts` (a sibling directory of this project, read-only) at `/ports`; that is the ports tree the recipe sync reads. Alternatively set `PortsTree__GitUrl` (and optionally `PortsTree__Branch`, default `main`) and the server clones the repository itself into `PortsTree:Path` (blobless clone) and, on later syncs, fetches and hard-resets to the branch tip; this is what the Azure deployment uses. Only one sync can run at a time per server instance; a second request gets 409.

    cp .env.example .env
    docker compose up -d --build

Two images are built: `api` (root `Dockerfile`: the server on Kestrel, port 8080, non-root, with the Haiku tools) and `web` (`nginx/Dockerfile`: the SPA built into a non-root, read-only nginx on port 8080). Only `web` is published; it serves the SPA and proxies `/api/`, `/signin-github` and the `/repo/<platform>/` download and index paths to the `api` service. The upstream is set by `API_UPSTREAM_HOST`, `API_UPSTREAM_PORT` and `API_UPSTREAM_SCHEME` (defaults `api`, `8080`, `http`; in Azure the api app's `*.azurewebsites.net` host, `443`, `https`).

The SQL Server image is amd64 only and runs under Rosetta on Apple silicon, so the first start takes a while. The server applies EF migrations, creates the blob container and seeds the roles and admin account at startup.

| What | URL |
|---|---|
| Web UI and API (through nginx) | http://localhost:8080 |
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

A job is created `Queued`. A client's `POST /api/client/jobs/next` atomically claims the Queued job for its platform whose port has the highest build priority (see below), oldest first among equals: the job becomes `InProgress`, records the client, claim time and lease expiry (now plus `Jobs:LeaseHours`, default 72), and increments `attempts`. The client uploads each package and then calls complete, which requires at least one uploaded package of the job's port and sets `Verifying`; nothing is published until the build is verified (next section). If the build or upload fails the client calls fail: the job becomes `Failed`, the claim is cleared, the summary and log are kept, and every user in the `Admin` role gets an email. If the client cannot build for a reason other than the recipe (Docker down, guest image missing, the user stopped it) it calls `POST /api/client/jobs/{id}/release` with `{ "reason", "log" }` instead: the job goes back to Queued as after an expired lease (the build becomes `Expired`, its uploads are deleted, completed builds keep counting, `attempts` stays incremented), the reason is kept as the summary with a `Released: ` prefix and the log as `logs/<jobId>.log`, no email is sent, and the response is 204; any client may claim it again, this one included. An admin can requeue a Failed or Cancelled job (it starts over, earlier builds no longer count), or cancel a Queued or AwaitingOverride one. At each `next` call, InProgress jobs whose lease has expired are returned to Queued (only that build slot: the build becomes `Expired` and its uploads are deleted; completed builds keep counting) and the admins are emailed; a late complete or fail from the old client then gets 409 (job no longer InProgress) or 403 (reclaimed by another client). Uploading a package whose platform and file name are already published, or already uploaded by the same build, is a no-op if the SHA-256 matches and a 409 if it differs. Logs are stored in blob storage as `logs/<jobId>.log`, packages as `<platform>/<sha256>/<fileName>`, and the ASP.NET Data Protection keys as `dataprotection/keys.xml` in the same container.

The package listing (`GET /api/repo/<platform>/packages?search=&page=&pageSize=`) is paged, newest first, and returns `{ items, total, page, pageSize }`; `pageSize` defaults to 50 and is capped at 200. The admin job (`GET /api/jobs`) and recipe (`GET /api/recipes`) lists are paged the same way. The repository page keeps the search and page in its URL (`?q=&page=`).

## Build verification

Every job is built twice, in two sessions, and published only when both builds have the same contents. Each claim is a build (`JobBuild`: client, claim and completion time, status, digest, differences); uploads go to staging (`StagedPackage`, the same content-addressed blobs) and become public `Package` rows only when the job is approved. Existing published packages and admin imports are trusted as they are.

- Matching. HaikuPorter builds are not reproducible (every hpkg embeds file times), so the server compares contents, not hpkg bytes. After a build completes it downloads each port package, runs `package list -i` (metadata: name, version, provides, requires, ...; any attribute whose key mentions a date or time is dropped), `package list -a` (every entry's path, type and permissions, size, symlink target and attribute names, types and sizes; the time column is dropped) and `package extract`, and hashes that canonical listing plus the SHA-256 of every file into the package's content digest; a build's digest covers its port packages. Two builds match when their digests are equal. A file the package tool cannot read (or a server without the tools) is compared byte for byte. Attribute values are not compared, only their names, types and sizes.
- Independence. Different clients are preferred: once a build completes, only other clients of the platform may claim the job's next build until the completion time plus that build's own duration (claim to completion); after that any client may, the first one included. The claim is one SQL transaction: an `UPDATE` of the job row with `UPDLOCK, READPAST` (so two clients never take the same build slot; a job has at most one open slot) whose `WHERE` excludes jobs with a completed build by this client whose window has not passed, plus the insert of the build row. When the matching builds came from one client the job and its packages are marked self-verified; the public listing shows "verified: 2 clients" or "self-verified", and the Jobs and Packages tabs and the client jobs API show the flag.
- Mismatch. A third build decides: whichever result two builds agree on is published (the earliest of them). If all three differ the job fails and the admins are emailed.
- Timestamp-only mismatch. For each differing file the server records a report (path, both sizes, offsets and short snippets of the differing byte runs, capped). If every differing file has the same size on both sides and every differing run is at most 32 bytes and lies inside a date or time on both sides (`Nov 17 2026` as from `__DATE__`, `21:13:05`, asctime and `date` output, ISO 8601, ten- or thirteen-digit epoch values from 2001 to 2033; see `Timestamps` in `Services/PackageDigest.cs`), no third build is queued: the job becomes `AwaitingOverride` and the admins are emailed. On the Jobs tab an admin sees the differences and can "Approve anyway", choosing the build to publish and giving a reason (who, when, why and which build are recorded; the job shows as overridden, never as self-verified). Ticking "this port embeds dates" flags the port: its later timestamp-only mismatches are approved automatically (recorded as `EmbedsDates` on the job). The Recipes tab shows the flag with Clear. Binary timestamps (a little-endian `time_t`) are not recognised and count as real differences.
- Extras. Packages a build uploads that are not its port's (dependencies the build made on the way) are recorded as "extra, not published" and their bytes are not kept. A package belongs to the port when its name is the port name or starts with `<port>_`, unless a longer port name in the recipes claims it (so `gcc_bootstrap_x` is not `gcc`'s); a subpackage renamed with `PACKAGE_NAME_*` to something else counts as an extra.
- A build the client reports as failed fails the job, as before; the staged blobs of all its builds are deleted. On approval the other builds' staged blobs are deleted (a blob shared with a published package stays).

Settings: `Verification:RequiredMatches` (default 2; 1 publishes the first build, so no verification) and `Verification:Schedule` (default true; the verifier runs every 10 seconds and right after each completion). Job statuses: `Queued`, `InProgress`, `Verifying`, `AwaitingOverride`, `Succeeded`, `Failed`, `Cancelled`; build statuses: `InProgress`, `Completed`, `Failed`, `Expired`, `Discarded`. `client/scripts/mock-build.sh` takes `MOCK_TIMESTAMP=1` (adds a timestamp-like difference) and `MOCK_VARIANT=<text>` (a real one) for trying this out.

## Automatic updates

The admin page's Recipes tab has an Automatic updates window (API: `GET`/`PUT /api/settings/auto-update`, `POST /api/settings/auto-update/run`, Admin only), modelled on RenkuOSPorts' `renkuos/bootstrap/arm64/update-packages.sh`. Settings, stored in the database: Enabled (default off), interval in hours (default 6, at least 1), and the platforms to queue for; the window also shows the last run, the last processed commit and what that run did. Each run, every interval while enabled or on Run now:

- Updates the ports tree as the recipe sync does (fetch and hard-reset with `PortsTree:GitUrl`; otherwise the checkout is read as it is, since the compose mount is read-only), runs the recipe sync, then lists the files changed from the last processed commit to `HEAD`. The first run only records `HEAD`.
- Maps changed files to ports: a changed `<category>/<dir>/<port>-<version>.recipe` gives its port; a file under that directory's `patches/` or `additional-files/` gives every port with a recipe there.
- For each changed port and platform, queues the port's newest version (through the same code as `POST /api/jobs`, so a platform with a Queued or InProgress job for it is skipped) only if the platform already publishes a package of that port and no published file starts with `<port>-<version>-<REVISION>-`. Other cases are skipped with the reason ("not built on <platform> yet", "already published; bump REVISION to rebuild").
- The processed commit moves on when the run finishes, skips included; a git or database error keeps the old commit and is shown. One atomic update of the settings row claims a run, so only one server instance runs it (a claim older than an hour is taken over). Jobs it queues show "auto-update" as queued by.

## Build priority

A port many outstanding ports depend on is built first. The admin page's Recipes tab has a Build priority window (API, Admin only: `GET /api/priorities`, `POST /api/priorities/recompute`, `POST /api/priorities/<platform>/queue-top` with `{ count }`, 1 to 200, default 20, `PUT` and `DELETE /api/priorities/<platform>/<portName>/pin`), and the recipe list takes `platform` and `sort=priority` (`GET /api/recipes?platform=arm64&sort=priority`) and then shows each recipe's priority and direct dependents there.

- Every recipe is evaluated for each platform the way HaikuPorter does it: bash sources the recipe with `portName`, `portVersion`, `targetArchitecture`, `secondaryArchSuffix` and the other port variables set, then prints its `ARCHITECTURES`, `PROVIDES*`, `REQUIRES*`, `BUILD_REQUIRES`, `BUILD_PREREQUIRES` and `PACKAGE_NAME*` (`Services/RecipeEvaluator.cs`, the bash script of RenkuOSPorts' `renkuos/bootstrap/arm64/phase2/eval-recipes.py` driven from C#, so the image needs bash but not Python). Evaluations run in parallel and are cached per recipe file SHA-256 in blob `priorities/<platform>/eval.json`, so later runs only evaluate changed recipes.
- The graph follows `resolve.py` there: a port's recipe is its newest stable one on the platform, else newest untested, else newest. A port is outstanding on a platform if that recipe is stable there and no package of the port is published there. Port X needs port Y if a `BUILD_REQUIRES` or `BUILD_PREREQUIRES` entry of X, or a runtime `REQUIRES` of a package those pull in (transitively), resolves to a package of Y; names only Haiku itself provides, and names no recipe provides, are ignored. A port's priority is the number of outstanding ports that need it directly or through other outstanding ports (a published intermediate already satisfies its dependents); each is counted once, so cycles end. Dependents is the number of outstanding ports that need it directly.
- Results are stored per platform and port (`PortPriorities`), with the direct needs edges of the outstanding ports (`PortDependencies`, replaced in the same transaction; about 137k edges for arm64 with nothing published). The job claim orders by the job's port priority (no row counts as 0), then queue time. An admin can pin a port on a platform (Move to top in the recipe list, the `pin` API above): pinned ports come before all computed priorities in the claim, the queue position, `sort=priority` and queue top N, the most recently pinned first, and a pin stays across recomputes until it is removed (Unpin). Queue top N queues, through the same code as `POST /api/jobs`, the highest-priority outstanding ports that have nothing published, queued or building on the platform, using the recipe the graph chose.
- Priorities are recomputed at startup, right after a recipe sync (also the automatic update's), 5 minutes after the last package was published or deleted, and on Recompute. A lease on blob `priorities/lock` lets one instance compute at a time. A full first evaluation of the ~4000 recipes takes about 5 s per platform in the Linux image on 10 cores (about 90 s on macOS, where .NET starts processes much more slowly); a run with no changed recipes reads and hashes the files in well under a second, and the graph takes about 0.3 s.

## Queue view for build clients

A build client can see the queue with its own bearer token (the SPA cookie does not work here, and the token does not open the admin API). Both endpoints are read-only and show client names and owner display names only, never emails, user ids, secrets or logs.

- `GET /api/client/jobs?status=&platform=&scope=&page=&pageSize=` returns a page (`items`, `total`, `page`, `pageSize`; 50 per page by default, 1 to 200) of jobs on every platform, by default Queued and InProgress, in claim order (finished statuses newest first). `scope=mine` keeps the jobs claimed by this client, `scope=owner` those claimed by any client of the same owner. Each job has its port's `priority`, `claimedBy` (client name), `mine`, `sameOwner` (claimed by another client of the owner), `queuePosition` (Queued jobs on the caller's platform: the 1-based place in the order the claim takes; both use one SQL ORDER BY, `BuildClientApi.ClaimOrder`) and the dependency tree from `PortDependencies`: `waitingOn` (up to 10 ports, nearest first, that the job's port needs directly or transitively and that are queued or building on its platform) with `waitingOnCount`, `inMyChain` (it needs, or is needed by, what this client is building), `inOwnerChain` (the same for the owner's other clients) and, on this client's own current job, `unblocks` (queued jobs that need it). Edges exist only from outstanding ports, so a rebuild of a published port waits on nothing; a lease that has expired shows InProgress until the next claim requeues it.
- `GET /api/client/me` returns `id`, `name`, `platform`, `owner`, `currentJob` (as above, or null) and `ownerClients` (`name`, `platform`, `lastSeenAt`, `currentJobId`).

The client prints them with the same configuration as the build loop (`BuildCommand` is not needed) and exits; with no command it runs the build loop as before:

    renkuports-client jobs [--mine|--owner] [--status Queued] [--platform arm64] [--page 2]
    renkuports-client status

`jobs` marks jobs held by this client with `*` and by another client of the owner with `o`, shows `#position` or the claiming client, the chain (`mine`, `owner`, `unblocks N`) and what each job is waiting on:

       ID  PORT-VERSION     PLATFORM  STATUS      POS/CLAIMED-BY  CHAIN              WAITING ON
    *  1   zlib-1.3.2       arm64     InProgress  m1-builder      owner, unblocks 1
    o  2   foo-1.10         arm64     InProgress  m2-builder      mine               zlib
       3   openexr30-3.0.5  arm64     Queued      #1              mine+owner         foo, zlib
       4   bar-2.0          arm64     Queued      #2

## Package repository index (pkgman)

Each platform has a pkgman repository at `<PublicBaseUrl>/repo/<platform>`: `repo.info`, `repo` and `repo.sha256` (404 until the first build) next to the existing `packages/<file>`. On Haiku: `pkgman add-repo https://<host>/repo/arm64`. The repository page shows that command once the platform's `repo.info` exists.

How it is built (`Services/RepoIndexer.cs`, after `renkuos/bootstrap/arm64/publish-repo.sh` in RenkuOSPorts):

- Input: the published packages (job `Succeeded`) of the platform, the newest version and revision of each package name (file names are `<name>-<version>-<revision>-<arch>.hpkg`), without `_debuginfo` and `_source` packages.
- Each package new to the index is downloaded and checked with Haiku's `package` tool: it must be a valid hpkg, its vendor must equal `RepoIndex:Vendor` (package_repo refuses others), its architecture must be the platform's, `any` or `source`, and its file name must be its canonical name (pkgman downloads `packages/<canonical name>`). A package that fails is left out with the reason, shown on the admin dashboard; the mock packages from `mock-build.sh` are always left out this way.
- The first build runs `package_repo create`. Later builds run `package_repo update -t` on the previous `repo`, so only new packages are downloaded; if that fails, the indexer falls back to a full `create`. The files go to blob `<platform>/index/`, `repo` before `repo.sha256`, so a mismatched pair is visible only between those two uploads (pkgman then reports a checksum error and a retry succeeds). `state.json` there records what the index was built from and what was excluded.
- A succeeded job marks its platform dirty; the rebuild runs once no further job succeeded for `RepoIndex:DebounceSeconds` (default 60). Every platform is also checked at startup, which does nothing if its inputs are unchanged. The Reindex button on the dashboard (`POST /api/repo/<platform>/reindex`, Admin) forces a full rebuild. A blob lease on `<platform>/index/lock` lets one instance at a time build a platform.
- An admin can delete a published package on the admin page's Packages tab (`DELETE /api/repo/<platform>/packages/<file>`, Admin, 204). The row and its blob are removed at once (the blob only if no other row references it), the deletion is logged with the admin's name, and the platform is marked dirty, so the next rebuild drops the package from the index: each build reads its input set from the database and `package_repo update -t` keeps only the listed names. If the deleted package was the newest version of its name, the next older published version takes its place. The job that built it keeps its record. A build already running when the package is deleted may still include it; the rebuild after it removes it.
- Settings: `RepoIndex:PublicBaseUrl` (the site's public origin, written into `repo.info` as the base URL; default `http://localhost:8080`), `RepoIndex:Name` (`RenkuOSPorts`), `RepoIndex:Vendor` (`Haiku Project`), `RepoIndex:Summary` (`RenkuOSPorts packages for <platform>`), `RepoIndex:Priority` (1), `RepoIndex:IdentifierPrefix` (`tag:renkuos.org,2026:repositories/renkuosports`, `/<platform>` is appended), `RepoIndex:ToolsPath` (`/opt/haiku-tools`).

The tools are Linux binaries. The Dockerfile builds them from Haiku source (the `haiku-tools` stage: Haiku's jam from `haiku/buildtools`, then `configure --host-only` and `jam '<build>package_repo' '<build>package'` on the RenkuOS source commit in `HAIKU_COMMIT`) for the image's own architecture, so the arm64 image built here and the amd64 image built for App Service each get matching tools. Without the tools (for example `dotnet run` on macOS) the server works normally but does not build indexes; the dashboard says so and the Reindex endpoint answers 503.

## Importing from another repository

To backfill packages built elsewhere, an admin can import from any pkgman repository on the admin page's Import tab (API: `POST /api/imports` with `{ sourceUrl, platform, dryRun }`, then `GET /api/imports/<id>` for progress, `GET /api/imports` for history, `POST /api/imports/<id>/cancel`). `sourceUrl` is the repository's base URL, the one you would give `pkgman add-repo`; only http and https are accepted.

- The importer downloads `<url>/repo`, checks it against `<url>/repo.sha256` when that exists, and reads it with `package_repo list -f -v`, which prints the repository's architecture and, for every package, its canonical file name and the SHA-256 checksum the repository records for the file. The repository's architecture must be the chosen platform.
- Packages whose file name this platform does not have yet are the gaps. A dry run lists them and stops.
- Otherwise each gap is downloaded from `<url>/packages/<file>` (4 at a time, at most 4 GiB each, aborted if no data arrives for 60 s), compared with the repository's checksum, checked with the `package` tool like the indexer does (vendor, architecture, canonical name), and stored through the same code path as client uploads (blob `<platform>/<sha256>/<file>`). A package that fails any step is listed as skipped with the reason; the import goes on. Existing packages are never replaced: a name already taken here is not a gap, and a race with an upload of different content is skipped as "exists with different content".
- Imported packages are public at once (they have no job) and list "import: <host>" as builder. The port name is the longest known recipe name that prefixes the package name (`inih_devel` gives `inih`), and the recipe date that recipe's; the build date is the import time. The platform's index is rebuilt after the import.
- One import per platform runs at a time (409 otherwise). Cancel stops after the files in progress. Running the same import again copies only what is still missing. An import whose server stopped is marked Failed when the next one starts; it can simply be run again.

## Maintenance

The admin page's Maintenance tab deletes data nothing needs any more. Each tool first runs as a preview (a dry run that reports count and bytes without deleting); Delete is enabled only after a preview with the same inputs and asks for confirmation. Only one cleanup runs at a time per instance (409 otherwise). API (Admin only; `dryRun` defaults to true when omitted):

- `POST /api/admin/cleanup/logs` with `{ olderThanDays, dryRun }` (at least 1) returns `{ count, bytes }`: it deletes `logs/<jobId>.log` of Succeeded, Failed and Cancelled jobs completed before then, and clears their log flag. Active jobs keep their logs.
- `POST /api/admin/cleanup/files` with `{ orphans, superseded: { keep } | null, staged, dryRun }` returns `{ orphans, superseded, staged, total }`, each `{ count, bytes }` or null when not chosen; `bytes` is always the storage freed. Superseded runs first, then staged, then orphans, so a freed file is counted once. Superseded (`count`: package rows) removes, per platform and port, the published packages of all but the newest `keep` versions (version from the file name `name-version-revision-arch.hpkg`, ordered like recipe versions; all revisions and subpackages of a version go together), like the admin package delete, then rebuilds the affected indexes. Staged (`count`: staged rows) clears the stored file of uploads whose build was Failed, Expired or Discarded or whose job is finished; after it, a Failed job's builds can no longer be approved. Orphans (`count`: files) deletes `logs/<id>.log` of jobs that no longer exist and `<platform>/<sha256>/<file>` blobs no package or staged upload names, if they were last modified more than 24 hours ago (an upload writes its file before its row). A package file is deleted only when no package row and no upload of an unfinished job still names it (identical uploads share one file). Index, priority, data protection and any other blobs are never touched.

### API tokens (build issues)

An automated agent can work the build-issue queue with a personal API token instead of an admin session. Create one on the admin page's Users tab (API tokens card) or with `POST /api/admin/tokens` `{ name, expiresInDays }` (1 to 90, default 30; at most 10 active per user); the response holds the token (`rpat_…`) once, and only its SHA-256 is stored. `GET /api/admin/tokens` lists your own tokens and `DELETE /api/admin/tokens/{id}` revokes one; these need the signed-in cookie, never a token. A token has the `issues` scope: it is accepted only by `GET /api/issues`, `GET /api/issues/{id}`, `POST /api/issues/{id}/resolve`, `POST /api/issues/{id}/escalate` and `GET /api/jobs/{id}/log`, and every other endpoint answers 401. It acts as its creator, and only while that user is still an admin who may sign in (confirmed, not disabled or locked out), so removing the Admin role or disabling the user stops it at once. Notes, `resolvedBy` and logs record the actor as `<user> via token '<name>'`. Send it as a bearer token:

```sh
curl -H "Authorization: Bearer rpat_..." https://ports.example.org/api/issues
```

## Tests

The xUnit integration tests run the real server against the compose SQL Server and Azurite, so start those first. The test database is `RenkuPortsTests` and the blob container `packages-test`; the SA password comes from `RENKUPORTS_TEST_SA_PASSWORD`, defaulting to the one in `.env.example`.

    docker compose up -d sql azurite
    dotnet test tests/RenkuPorts.Server.Tests

Two tests run the real `package` and `package_repo` tools on real hpkgs: index creation plus an incremental `update -t`, and a repository import from a source repository built with `package_repo create` and served over HTTP (dry run, import, re-run, a corrupt file skipped on its checksum, the imported packages in the listing and the regenerated index). On macOS they are reported as skipped. Run the whole suite in a Linux container built from the Dockerfile's `tools-test` stage, which has the SDK and the tools and requires them to be present, on the compose network (so `sql` and `azurite` must be up), with a directory of real packages (it uses `packaging-26.2-1-any.hpkg`, `markupsafe-2.1.3-3-arm64.hpkg` and `libbase58_devel-0.1.4-2-arm64.hpkg`):

    tests/run-haiku-tools-test.sh ../build-arm64/build-vm/repo/packages

## Configuration notes

Besides the keys in `appsettings.json`: `Database:MigrateOnStartup` (default true; set false to apply migrations from a pipeline, e.g. `dotnet ef migrations script --idempotent -p src/RenkuPorts.Server -o migrate.sql`), `Blob:ServiceUri` and `Email:AzureCommunication:Endpoint` (use `DefaultAzureCredential` instead of the connection strings, which remain the fallback), `PortsTree:GitUrl` and `PortsTree:Branch`, and `APPLICATIONINSIGHTS_CONNECTION_STRING` (when set, telemetry goes to Application Insights through OpenTelemetry). SQL access uses EF's retry-on-failure; a transient error after the job-claim statement has committed can make the retry claim a second job, and that extra job simply waits out its lease. The server applies `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from any hop (`UseForwardedHeaders` in `Program.cs`), so the API must be reachable only through nginx; `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is no longer needed.

## CI

`.github/workflows/ci.yml` runs only when a tag starting with `v` is pushed. It builds and tests the server (against SQL Server and Azurite from the compose file), builds the SPA, builds both images for `linux/amd64` and checks the nginx config with `nginx -t`. The slow `haiku-tools` stage is cached in the GitHub Actions cache. It then pushes `<ACR_LOGIN_SERVER>/renkuos-ports/web` and `<ACR_LOGIN_SERVER>/renkuos-ports/api`, each tagged with the commit SHA and `latest`. It needs the repository secrets `ACR_LOGIN_SERVER` (for example `myregistry.azurecr.io`), `ACR_USERNAME` and `ACR_SECRET` (an ACR token with push rights to those two repositories).

## Versioning and releases

The repository has one version for the server, the client and the web UI, taken from the newest `vMAJOR.MINOR.PATCH` git tag by `scripts/version.sh`, the only place the mapping lives:

| Checkout | Version |
|---|---|
| On tag `v1.2.3`, clean tree | `1.2.3` |
| 5 commits after `v1.2.3` | `1.2.4-dev.5+gabc1234` (the next patch as a pre-release, so it sorts after `1.2.3`) |
| Uncommitted changes | `1.2.4-dev.0+gabc1234.dirty` |
| No version tag yet | `0.0.0-dev.<commits>+gabc1234` |
| Not a git checkout, or nothing passed | `0.0.0-dev` (the default in `Directory.Build.props`) |

Pre-release tags such as `v1.3.0-rc1` are ignored. Builds pass the version explicitly with `-p:Version=...`; the Docker build context has no git history, so the image gets it as the `VERSION` build argument:

    VERSION=$(scripts/version.sh) docker compose up -d --build
    dotnet publish src/RenkuPorts.Client -c Release -p:Version=$(scripts/version.sh) ...

To cut a release:

    git tag -a v0.1.0 -m "RenkuOS Ports 0.1.0"
    git push origin v0.1.0

`.github/workflows/release.yml` then checks that the tag and `scripts/version.sh` agree, builds the server, client and web with that version, and creates the GitHub Release for the tag with the self-contained single-file build client for `osx-arm64`, `linux-x64`, `linux-arm64` and `win-x64` (`renkuports-client-<version>-<rid>`, `chmod +x` after download), `appsettings.example.json` and `SHA256SUMS`. Container images are not published by it.

Where to see the version: the site footer (`v1.2.3`, from the API, so it is always the deployed server's), `GET /api/version` (`{"version":"1.2.4-dev.5","commit":"abc1234"}`; `commit` is null for a tagged release), the server's first log line, and `renkuports-client --version`. The client also logs it at start and sends `User-Agent: renkuports-client/<version>`, which the server logs when the client gets a token. `tests/version-test.sh` checks the mapping against a throwaway git repository.

## Known gaps

- Managed identity access to Blob Storage and Communication Services, and Application Insights, are implemented but not tested against real Azure.
- The recipe-sync lock is per server instance, so two instances could sync at once.
- Package downloads are proxied through the web app and do not support HTTP range requests.
- Build logs are per job: a job's second build overwrites the first build's log.
- Only arm64 has a real build script; `x86`, `x86_64` and `riscv64` clients need their own `BuildCommand`.
