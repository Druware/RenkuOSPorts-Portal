# Mirrors and pkgman versioning

**Status:** research for a proposed future project, written 2026-11-19; the design that follows from it is in [r2-mirror.md](r2-mirror.md). Nothing described here as planned has been implemented.

Research for the plan "server in Azure, repository master on Cloudflare R2, static full-repository mirrors elsewhere that serve the bulk of downloads and pull what changed once a month". Written 2026-11-19.

Evidence tags used throughout: **[V]** verified by reading source (path:line), **[T]** tested on a Haiku box earlier in this project (hrev60072 x86_64, recorded in the project memory), **[W]** taken from a web page or observed with curl today, **[A]** assumed or inferred, not verified.

Source trees: the RenkuOS fork at `/Users/arsatori/Sources/local/RenkuOS/Source` (HEAD d59b4065ac, 2026-11-17), referred to below by paths relative to it (`src/kits/package/...`). It has pkgman additions that upstream Haiku does not have (`pkgman reinstall`, `full-sync --reinstall`, optional reinstalls that skip a failed download, `src/kits/package/manager/PackageManager.cpp:1076-1110`); I could not diff against upstream here, so everything below that does not touch those additions is believed identical to upstream but was read in the fork. RenkuPorts paths are relative to `/Users/arsatori/Sources/local/RenkuOS/RenkuOSPorts-Public`.

## 1. Summary of risks, ranked

1. **Same file name, different bytes.** pkgman identifies a package by file name and checks the downloaded bytes against the SHA-256 in the repository index; any mismatch is a hard failure for that client (section 2.4). RenkuPorts never overwrites a *live* name (`Services/PackageStore.cs:23-50`), but it forgets a name the moment its row is deleted (superseded cleanup, admin delete, danger-zone reset). A rebuild after that can reuse the same `name-version-revision-arch.hpkg` with different bytes, and any mirror or R2 object that keeps "the file with that name" will then disagree with the new index. Mirror sync must therefore compare SHA-256, never just the name, and the server should refuse (or at least flag) reuse of a once-published name with different content.
2. **Index visible before the packages it names.** One missing package aborts the whole pkgman transaction with no retry and no fallback (section 2.4). The R2 plan (packages first, index last) is right; every mirror must do the same, and a mirror that cannot guarantee it should serve packages only and take its index from the master (section 6).
3. **`repo` and `repo.sha256` must be one consistent pair at every host a client can reach.** pkgman fetches the checksum first, then the repo, and fails the refresh if they differ (section 2.2). Haiku itself had a multi-day outage of exactly this kind when a CDN cached `repo` [W]. Mixed generations behind one hostname (DNS-balanced mirrors with different sync times) reproduce it.
4. **A stale index is not harmless.** `pkgman update` never downgrades, but `pkgman full-sync` does (section 2.1), and a higher-priority repository with an older version blocks updates from a lower-priority one (section 2.3). A mirror that serves a month-old index under the same repository name will cause downgrades for anyone who full-syncs against it after having used the master.
5. **Edge behaviour that makes pkgman hang or corrupt a download.** pkgman retries *forever, with no back-off* on HTTP 429, 410, 500, 503 and every 4xx/5xx not specially listed, and on 504/408 (section 2.4). It resumes a partial download with a `Range` request and appends whatever it gets back without checking for 206 (section 2.5); the Azure endpoint ignores `Range` today. R2 and nginx honour `Range`; rate-limit rules, WAF challenges and "origin unavailable" pages at a CDN are the dangerous part.
6. **Cleanup and index selection disagree about what a "version" is.** `SupersededAsync` counts versions per port, `RepoIndexer` picks the newest per package name, and the blob is deleted immediately. A subpackage that lags its port can lose its only file and drop out of the index (section 2.7). Not mirror-specific, but a mirror will faithfully copy the hole.
7. **No multi-mirror support in the client.** One repository has exactly one `baseurl`; there is no failover list, and two repositories with the same `name` overwrite each other's config (section 2.2). Mirror selection has to happen on the server side (redirect or DNS), not in the client configuration.
8. **Integrity rests on TLS and the index.** There is no repository signature anywhere in the package kit (section 2.2). The index is the only authenticator for package bytes, so whoever serves the index is trusted completely; a mirror that only serves `packages/` is not (section 6.1).
9. **Smaller items:** https needs a libbnetapi built with OpenSSL [T]; use 302/303/307, never 308 [T]; the server's `VersionComparer` is a shortcut that differs from libsolv's comparator in separator handling and case (section 2.1); file names with `~` and `+` pass through redirects unencoded and need a quick test on R2.

## 2. Findings

### 2.1 Versions (question 1)

**Format and comparison.** `BPackageVersion` is `major[.minor[.micro]][~preRelease][-revision]`; components are lower-cased on assignment. `Compare` uses natural ordering on major, minor, micro; then pre-release, where *empty is greater than any non-empty* ("R1" is newer than "R1~rc2"); then the integer revision as a plain subtraction. `ToString` drops the revision when it is 0. [V] `src/kits/package/PackageVersion.cpp:90-121` (compare), `:124-145` (ToString, revision omitted at `:141`), `:148-162` (lower-casing).

**What the solver actually compares.** The solver does not use `BPackageVersion::Compare`; it hands libsolv the string `Version().ToString()` as the solvable's `evr` and libsolv compares with its Haiku variant. That splits at the first `~`, compares the main parts, treats "no pre-release" as greatest, and compares the remainder alternately as non-digit runs (by `strncmp`, then length, a non-digit run beats a missing one) and digit runs (leading zeros stripped, longer is larger). The revision is just the trailing `-N` of that string. [V] `src/libs/libsolv/ext/repo_haiku.cpp:133` (`solvable->evr`), `src/libs/libsolv/solv/evr.c:201-289`. The solvable name is prefixed `pkg:` so that provides never match by accident (`repo_haiku.cpp:127-133`).

**Provides, requires, compat versions.** A package provides `pkg:name = version` plus every entry of its provides list as `name = version`, and when the provide carries a compatible-version it becomes a `REL_COMPAT` relation (`repo_haiku.cpp:20-36, 52-58`). Requires, conflicts, supplements, freshens and replaces become libsolv dependencies with the operator mapped one to one (`repo_haiku.cpp:62-112`). Consequence for repositories: the index is a closed world for the solver. A package in the index whose requirement is only satisfied by a package that is *not* in the index (excluded, failed, not yet built) cannot be installed. A mirror that copies the index and every file it names as one unit preserves closure; a mirror that mixes generations does not. [V]

**`update` versus `full-sync`.** `pkgman update` runs libsolv `SOLVER_UPDATE`; `pkgman full-sync` runs `SOLVER_DISTUPGRADE` (`src/kits/package/solver/libsolv/LibsolvSolver.cpp:431`, `:465`). Solver flags set: `SPLITPROVIDES`, `BEST_OBEY_POLICY` (`:654-655`). In libsolv, ordinary solving has `allowdowngrade` off, so an installed 2.0 is never replaced by 1.9 (`src/libs/libsolv/solv/policy.c:715-722`, flag set at `solver.c:1569`), and vendor changes are illegal unless the vendor strings are equal (`policy.c:688-708`; RenkuPorts enforces one vendor per repository, `Services/RepoIndexer.cs:75`). Dist-upgrade mode defaults `dup_allowdowngrade = 1` (`solver.c:1425`), so `full-sync` moves every package to whatever the repositories offer, *including downward*. The usage text says so: "downgrades or removes packages, if necessary" (`src/bin/pkgman/command_full_sync.cpp:27-30`). Packages that no repository has ("orphans") are kept, because the Haiku solver does not set the drop-orphans flag (grep of `LibsolvSolver.cpp` finds none). [V] for all of this; the behaviour of libsolv pruning in each mode is from reading `policy.c` and `solver.c`, not from a run.

**A repository that drops older versions.** Harmless for installed machines (they keep what they have; the package daemon keeps its own state directories). Only the version in the index can be installed, and `pkgman install foo-1.2` for a dropped version fails at solve time. RenkuPorts goes further: the index holds exactly one version per package name, the newest by `VersionComparer` then revision (`Services/RepoIndexer.cs:36-54`), so history never reaches clients. This is what makes monthly mirrors cheap: a client N months behind jumps straight to the newest file; intermediate versions are never requested [V].

**A mirror whose index is newer or older than the master.** Newer: nothing breaks as long as its files exist. Older: `update` simply sees nothing newer; `full-sync` downgrades to the older index's versions, which only works if those old files are still served; a client that has a *newer* package from the master installed and runs `full-sync` against a stale mirror is downgraded [V by the rules above, A that nobody has run it end to end].

**Equal versions, different bytes.** libsolv sees equal `evr` and treats the candidate as "same", so a client that already has `foo-1.2-3` never re-downloads a rebuilt `foo-1.2-3`; a client installing fresh gets whatever the index checksum says and fails if the served bytes differ (section 2.4). A rebuild must bump the revision to propagate. [V for compare semantics; A for the end-to-end effect]

**`VersionComparer` in RenkuPorts versus libsolv.** `Services/Platforms.cs:25-60` tokenises into digit and letter runs and ignores separators, compares letters ordinally and case-sensitively, and compares revisions separately in `Select`. libsolv compares separators as text, and the version has been lower-cased before it gets there. Differences appear only for unusual versions (case differences, `1.0` versus `1-0`); the code already carries a `shortcut:` note about it. The risk is that the server picks as "newest" something libsolv would rank lower, which only hides a version from clients. Low. [V]

### 2.2 Repository files, refresh, identifier and baseurl (question 2)

**Files.** `repo` (HPKR catalogue; embeds the repository info and, per package, its attributes, canonical file name and SHA-256 checksum), `repo.info` (text: name, vendor, summary, priority, baseurl, identifier, architecture; all but baseurl required, `src/kits/package/RepositoryInfo.cpp:360-405`), `repo.sha256` (64 hex characters, only the first 64 bytes are read, compared case-insensitively; `src/kits/package/ChecksumAccessors.cpp:47-69`, `ValidateChecksumJob.cpp:59`), and `packages/<canonical file name>`. `package_repo update -t` keys entries by file name and keeps existing entries unread (`src/bin/package_repo/command_update.cpp:31-40, 262-266, 318-330`), which is what RenkuPorts uses to avoid re-reading packages (`Services/RepoIndexer.cs:93-94`). [V]

**Signature.** None. There is no `repo.sig`, no key handling and no signature check in `src/kits/package`, `src/bin/pkgman` or `src/bin/package_repo` (grep for repo.sig, gpg, signature finds only app signatures). `repo.sha256` is fetched from the same host as `repo` and so protects against truncation, caching accidents and mirror skew, not against a hostile host. Package integrity comes from the checksum stored in the index (`PackageManager.cpp:1076-1078` passes `package->Info().Checksum()`; `DownloadFileRequest.cpp` queues a `ValidateChecksumJob` that fails on mismatch, `ValidateChecksumJob.h:31` default `failIfChecksumsDontMatch = true`). [V]

**Refresh algorithm.** `BRefreshRepositoryRequest`:
1. GET `<baseurl>/repo.sha256` into a temp file.
2. SHA-256 the local cache file for this repository (a missing cache counts as "no checksum"), compare to the fetched value (`RefreshRepositoryRequest.cpp:51-107`).
3. If equal, done; the `repo` is *not* downloaded. If different, GET `<baseurl>/repo`, require its SHA-256 to equal the checksum fetched in step 1 (fails with "Checksum error" otherwise), move it into the cache (`:110-185`, `ActivateRepositoryCacheJob.cpp:39-49`).

The checksum fetched at step 1 is reused at step 3, so the two requests are not atomic; a publish between them yields a mismatch and a failed refresh. There is no conditional request (no ETag / If-None-Match / If-Modified-Since handling in either libnetservices copy; grep) and no HEAD request. A forum post from the Haiku admins describes the package kit as checking a HEAD request's date [W]; the code in this tree does not do that, so either that description is imprecise or it refers to a different client; I treat the source as authoritative. [V]

**When refresh is skipped or tolerated.** `pkgman install` has `-R/--no-refresh` (`src/bin/pkgman/command_install.cpp:49-50, 100`); `update`, `full-sync`, `reinstall` and `verify` always refresh (`PackageManager.cpp:533-536, 590-592`). A failed refresh is a *warning*; pkgman then uses the existing cache if there is one (`PackageManager.cpp:1505-1516`) and skips the repository only if there is none. So a broken index normally results in a silent stale view, not an error. [V]

**`baseurl`, `url`, `identifier`.** The local config (`/system/settings/package-repositories/<name>`) has `baseurl` (where files are fetched from) and `identifier` (plus a deprecated duplicate key `url`; `RepositoryConfig.cpp:26-37, 102-119, 184-203`); both are required or the config is `B_BAD_DATA`. `pkgman add-repo <url>` downloads `<url>/repo.info`, and the stored `baseurl` is *the URL typed*, not the one inside `repo.info` (`AddRepositoryRequest.cpp:55`, `ActivateRepositoryConfigJob.cpp:66-76`; [T] and stated in RenkuOS-on-Parallels/local-repo/README.md). The `baseurl` inside `repo.info` and inside `repo` is used only when `add-repo` is given an empty URL and by a few tools (`create_repository_config`, `get_package_dependencies`, the Repositories preferences). `identifier` names the *logical* repository ("consistent across mirrors"); pkgman only prints it (`command_list_repos.cpp:115, 136`); HaikuDepot matches it against HDS data and Haiku's own guidance is that a mirror carries the same identifier as the original [W: haiku-os.org/blog/apl/2020-07-11_repository_identification]. Config and cache files are named after the repository `name` from `repo.info`, and adding a repository whose name exists asks "overwrite?" (`ActivateRepositoryConfigJob.cpp:38-60`). [V]

**Priority.** The solver does *not* use the priority in the local config for remote repositories. `_AddRemoteRepository` builds the solver repository from the cache (`RepositoryBuilder.cpp:106` calls `SetTo(cache)`), which takes the priority from the `repo.info` embedded in `repo` (`SolverRepository.cpp:184`); libsolv gets `-1 - priority` (`LibsolvSolver.cpp:748`), so a *smaller* number wins. Changing priority therefore means a new `repo` file. [V]

**Several mirror base URLs for one repository.** Not supported. Config holds one `baseurl`, packages come from `<baseurl>/packages/<file>` (`RepositoryConfig.cpp:260-266`), there is no list and no failover. Haiku's own answer is a front-end redirector: `curl -I https://eu.hpkg.haiku-os.org/haikuports/master/x86_64/current/repo.sha256` returns `303` to `https://haikuports-repository.cdn.haiku-os.org/master/x86_64/current/repo.sha256` (nginx in front of S3-compatible storage; `ETag` and `Last-Modified` present; keys have no directory listing) and `repo.info` there lists `baseurl https://eu.hpkg.haiku-os.org/haikuports/master/x86_64/current`, `priority 1` [W, observed today]. [V/W]

### 2.3 Several repositories with the same package (priority)

libsolv prunes candidates to the highest-priority repository first (`policy.c:80-107`, `prune_to_highest_prio`), then picks the best version among what is left. Consequences [V from reading; A that every code path calls it]:
- Two repositories with *equal* priority: the highest version across both wins; with equal versions either may be chosen, which is fine when file names and bytes are identical.
- A higher-priority repository that is *older* hides newer versions in a lower-priority one; `update` will not install them and will not downgrade either.
- Because name, priority and identifier all come from `repo.info`, a second copy of the same repository as a "mirror" has the same name (collision, section 2.2) unless its `repo.info` is changed, which changes `repo` and `repo.sha256` and means the mirror generates its own index. Do not model mirrors as additional repositories.

### 2.4 pkgman when the index names a file the host does not have

`FetchFileJob::Execute` loops while the result is `B_IO_ERROR` or `B_DEV_TIMEOUT`, with no delay or counter (`src/kits/package/FetchFileJob.cpp:104-128`). Status mapping (`:156-212`): all 4xx and 5xx first become `B_IO_ERROR`; then 200/206 `B_OK`; 408 and 504 `B_DEV_TIMEOUT`; 501 `B_NOT_SUPPORTED`; 415 `B_UNKNOWN_MIME_TYPE`; 416 `ERANGE`; 401 `B_PERMISSION_DENIED`; 403/405/406 `B_NOT_ALLOWED`; **404 `B_NAME_NOT_FOUND`**; 502 `B_BAD_DATA`. Everything else keeps `B_IO_ERROR`. So:
- **404 or 403**: fails once ([T] for 403: one request, "Operation not allowed"). In `_PreparePackageChanges` the failure is `DIE "Failed to download package ..."` (`PackageManager.cpp:1110`) and aborts the entire transaction; nothing already downloaded is installed. The only exception is the fork's reinstall path, which warns and keeps the installed copy (`:1095-1108`).
- **410, 429, 500, 503, any other 4xx/5xx except those above**: the request is repeated immediately, forever [V, not tested]. A CDN rate limit or an origin outage behind the CDN becomes a busy loop on every client.
- **Checksum mismatch** (`B_BAD_DATA`) or `ERANGE`: the file is deleted; if the file came from a previous transaction's leftovers (`:1035-1062`) it is retried once from scratch, otherwise it fails (`:1076-1110`).
- A redirect (301/302/303/307, cross-host) is followed, 308 is not, about 7 hops [T].

### 2.5 Consistency windows (question 3)

- **Index updated before packages land.** Clients refresh, see new `repo`, solve, request `packages/new-file`, get 404, whole install or update fails with "Failed to download package". Persistent until the file arrives; no client-side retry besides running pkgman again.
- **Packages deleted while a cached index still references them.** Affects clients whose cache is older than the deletion: any `-R` install, any client that is offline until later, and every client in the window between master deleting and the client's next refresh. Clients with an up-to-date cache are unaffected, because `update` and `install` refresh first. A client that refreshed *before* the deletion and is mid-transaction hits a 404 on one file; the next run recovers.
- **Index pair skew.** In `RepoIndexer.BuildLockedAsync` the order is repo.info, repo, repo.sha256 (`Services/RepoIndexer.cs:299-303`), which is the better of the two orders: clients whose cache equals the old checksum see the unchanged old `repo.sha256` and do nothing; only clients two or more generations behind can fetch old sha plus new repo and fail the refresh (warning, retry next run). The reverse order would break every up-to-date client for the length of the window. Keep this order everywhere.
- **CDN caching.** Haiku's admin notice states the previous CDN "cached the repo file causing these checksum mismatches" [W: discuss.haiku-os.org/t/notice-package-repository-maintenance/16908]. The Azure endpoint already sends `Cache-Control: no-cache` for the three index files (`Endpoints/PublicEndpoints.cs:44`) and nothing for packages. Cloudflare's default cache is extension-based and `repo`, `repo.sha256`, `repo.info` have no cacheable extension (A; verify, and set `Cache-Control` on the R2 objects regardless); `.hpkg` is probably not in the default list either, so "cache everything" rules or explicit headers are needed to cache packages at all.
- **Immutability of names.** Intended, not guaranteed. `PackageStore` refuses to overwrite and reports `Conflict` for a live name with other bytes (`PackageStore.cs:46-48`), and the importer skips such names (`RepoImporter.cs:203`). But `Packages` has a unique `(Platform, FileName)` only while the row exists: `SupersededAsync`, the admin delete and `ResetAsync` delete rows and blobs (`Services/Cleanup.cs:118-139, 218-247`), after which the same name can be uploaded with different bytes. R2 objects under the 72 h grace rule would still hold the old bytes for a re-used name unless the publisher compares content.
- **Resume without a 206.** `FetchFileJob` sets `Range: bytes=<size>-` when the target file is non-empty and seeks to the end (`FetchFileJob.cpp:110-117`, header built at `src/kits/network/libnetservices/HttpRequest.cpp:993-1004`); nothing checks that the answer is 206. A server that answers 200 with the full body produces `partial + full file`, caught only by the checksum, then retried once. The RenkuPorts endpoint streams with `Results.Stream(...)` and no range processing (`PublicEndpoints.cs:63`; [`docs/azure-deployment.md:107`](../azure-deployment.md) says "without range support"). [V, A that ASP.NET's default is no range processing, which matches that doc]

### 2.6 Haiku and HaikuPorts practice (question 4)

What is established, and from where:
- Public URLs have the form `https://<mirror>.hpkg.haiku-os.org/<repo>/<branch>/<arch>/current` with `current` as a literal path segment, and the same layout is on the backing CDN host [W: blog/kallisti5 2018-09-11, discuss "Important repository changes", curl today]. Branch aliases were introduced so `r1beta5` and `master` paths resolve to the same data, implemented as server-side mapping or symlinks (a Haiku developer calls the r1beta2 directory "literally a symlink to the master one") [W].
- The front end ("hpkgbouncer", server header `Rocket`) answers `303` and the data lives in S3-compatible storage behind nginx [W, curl]. Packages are in an S3 bucket, no directory index [W].
- The HaikuPorts buildmaster publishes by running `hardlink_packages.py` from `/var/packages/repository/master/<arch>/current/packages/` to `/var/packages/build-packages/master/`; the doc says the package URLs are derived from the sha256 of the repository file and the file must not be edited after generation [W: haiku-os.org/docs/develop/build/repositories]. I could not find public documentation of versioned build directories, the mechanism that moves `current`, `rsync` use, or an official mirror programme; the one community mirror I found (cdn.haiku.nz) only documents its URL [W]. Those parts are **not established**.
- Community mirrors are plain static copies at another hostname with the same identifier [W]; the old CDN caching accident shows what happens when the index is cached independently of its pair.

So the closest official precedent is: one logical base URL, a thin redirector (303) to storage, `current` as the pointer, and the index served from the same place as the packages. There is no precedent I can verify for geo-distributed mirrors with independent sync times.

### 2.7 This project (question 5)

**RepoIndexer** (`Services/RepoIndexer.cs`).
- Inputs are the published packages of the platform (jobs succeeded or imported, `PublicEndpoints.cs:12`); `Select` keeps the newest version then revision per package name, skipping `_debuginfo` and `_source` and rejecting names that do not fit `name-version-revision-arch.hpkg` (`:36-59`). The index therefore changes whenever a package *name* gets a newer file, or the set of names changes, or `repo.info` text changes (`:225-227`).
- Rebuild is debounced (60 s default), guarded by a blob lease, incremental via `package_repo update -t` against the previous `repo` (`:93-94, 289-297`), and falls back to a full create on failure (`:229-234`). The index and its `state.json` (inputs as `fileName:sha256`, selected packages, excluded packages with reasons) live under `{platform}/index/` (`:153, 332`). State.json is effectively a per-generation manifest with SHA-256s but no sizes and no generation number.
- Packages that fail `CheckPackageAsync` (vendor, architecture, canonical name) are excluded **after** newest-selection, so a bad newest build removes the name from the index instead of falling back to the previous version (`:264-276`).
- If nothing qualifies, `repo`, `repo.sha256`, `repo.info` are *deleted* (`:280-285`) and served as 404; a mirror must read that as "no snapshot", not "delete everything".
- `repo.info` text carries `baseurl {PublicBaseUrl}/repo/{platform}`, `identifier {prefix}/{platform}`, `priority 1` (`:312-326`). Only users of the stored `baseurl` of `add-repo` see this value (section 2.2), but the same bytes must be served from every host for the identifier and checksums to match.

**Cleanup.SupersededAsync** (`Services/Cleanup.cs:118-143`). Keeps the newest N distinct versions per `(Platform, PortName)`, where the version is the second `-`-field of the file name, and deletes the *rows* and then the blobs of everything older, immediately (`DeleteIfUnreferencedAsync`, no grace). It then marks the platform's index dirty (`AdminEndpoints.cs:277-283`). Interactions with mirrors:
- Version churn: with keep=N the newest file of each name is always safe, so the *current* index is not hit, except where the port and the name disagree. Example: port `foo` has `foo-1.2-1` and `foo_devel-1.3-1`; with keep=1 the distinct versions for the port are {1.2, 1.3}, 1.3 is kept, `foo-1.2-1` is deleted although it is the newest `foo`, and after the rebuild `foo` is gone from the index. Group by package name (or ignore the port) to match what the index does. [V reading; not run]
- Window: between a new file's publication and the next index build (debounce plus build time) the index still names the older file; a cleanup in that window deletes a file the live index references. Exclude the names in the current `state.json` from deletion.
- No grace: nothing keeps the old bytes around for clients with a cached older index, and a name freed here can be re-used with different bytes (risk 1).

**PackageStore** (`Services/PackageStore.cs`). Content-addressed blob `{platform}/{sha256}/{fileName}` written with `If-None-Match: *` (`:60-79`), row insert guarded by a unique `(Platform, FileName)`, same name plus same bytes is a no-op, same name plus other bytes is `Conflict` (`:34-50`). The blob path embeds the hash, so *blobs* are immutable even when names are reused; the R2 layout `{platform}/packages/{file}` (named in the brief) has no hash in the key and so loses that property.

**RepoImporter** (`Services/RepoImporter.cs`). Pulls a source repository the way a mirror would: GET `repo`, GET `repo.sha256` (a mismatch aborts with "it may be changing, try again", `:99-101`), list with `package_repo list -f -v` and use each package's recorded checksum (`:12-36`), download only file names the platform lacks (`:106-107`), verify checksum, run the same package check as uploads, store. It is name-based: a name that exists with different content is skipped with "exists with different content" (`:203`). It keeps the importer from creating inconsistency but also means it would never repair a name that drifted. It is a useful model for the mirror puller (index first, verify pair, diff by name+checksum, files, then nothing else), but it is a one-shot import into the database, not a mirror, and it never deletes.

**PublicEndpoints** (`Endpoints/PublicEndpoints.cs`). Index files stream from the `{platform}/index/` blobs with `Cache-Control: no-cache` and content-length (`:35-52`); packages stream through the app, 404 if there is no row or no blob yet (`:54-69`), no `Cache-Control`, no `ETag`, no range support. Every download goes through the App Service plan ([`docs/azure-deployment.md:31`](../azure-deployment.md)).

**Monthly-pull consequences.**
- Version churn: the mirror needs one file per package name per generation. Between two monthly pulls every name that was rebuilt contributes one new file; older ones are not needed by any client. Monthly delta = the set of `(name, sha256)` pairs in the new index that the mirror lacks.
- Superseded deletions: harmless to a mirror (it keeps what it has), harmful only to anything that still needs the deleted file from the master (R2 fallback, clients with cached old indexes). A mirror must delete on its own schedule, not by mirroring the master's deletes immediately.
- Rebuilt package with a new revision: new file name, normal case; the old file is dropped from the index at once and deleted from R2 after the grace period. A mirror that pulled *before* the rebuild keeps the old file and serves it for any client holding the old index; fine.
- Rebuilt package with the same version and revision: pkgman will not update machines that have it, and fresh installs fail the checksum on any host that has the old bytes. Must not happen; section 7.
- Index-only changes (priority, summary, vendor, `baseurl` or identifier prefix, platform emptied and refilled): new `repo` and `repo.sha256` with no new packages; a mirror that polls only the package list misses them. The mirror's trigger must be `repo.sha256`/generation, not the file list.
- Failed or partial index builds keep the last good index (`:239-244`), so the master never publishes a half index; the mirror may therefore treat "repo.sha256 unchanged" as "nothing to do".

### 2.8 The planned R2 publisher, evaluated

The publisher evaluated here is the one proposed in [r2-mirror.md](r2-mirror.md). As described (bucket `{platform}/repo`, `repo.info`, `repo.sha256`, `packages/{file}`; packages first, then repo, repo.info, repo.sha256 last; deletes only after 72 h absent from the index). Good: the upload order is correct for pkgman, the grace period protects in-flight pulls, and R2 honours `Range` and returns 404 for missing keys. Not sufficient for downstream mirrors as it stands:
1. **No content check for existing keys.** "Already there, skip" by key name, or by size, is unsafe once names can be re-used (risk 1). Store the SHA-256 as object metadata (or in a manifest) and compare, overwriting or failing loudly on mismatch.
2. **Three-object index with no generation marker.** A mirror cannot atomically read a consistent `repo` + `repo.sha256` + `repo.info`; it can only retry until `sha256(repo) == repo.sha256` (which is what `RepoImporter` does). Add an immutable per-generation copy and a small `manifest.json` written last (section 6).
3. **Index deletion on an empty platform** (`RepoIndexer.cs:283`) must propagate as "index absent", not as a deletion of packages (they are deleted only after the grace period anyway).
4. **Grace period semantics.** 72 h is enough for a single pull that reads the index then fetches its files, as long as a failed pull is not resumed days later against the *old* index. A pull should re-read the index on each attempt. For clients with cached older indexes (offline, `-R`) a week or two is kinder and costs only the churn volume.
5. **Headers.** Set `Cache-Control: public, max-age=31536000, immutable` and `ETag`/checksum metadata on packages, `Cache-Control: no-cache` on the index and manifest, and `Content-Type` as the Azure endpoint does.

## 3. Mapping the questions to what pkgman needs from a mirror

A host that serves a repository for pkgman needs only: `GET /repo.sha256`, `GET /repo`, `GET /repo.info` (for `add-repo`), `GET /packages/<file>` with `Range` support, 404 for unknown names, no 429/5xx under load, one stable and consistent `(repo, repo.sha256)` pair, and bytes for every package that pair names. Everything else (versions, priorities, dependencies) is decided inside the client from the index.

## 4. Monthly-pull specifics worth stating once

- Because the index holds one version per name, the pull set is small and self-describing: the files in the new index not already on the mirror with the same SHA-256.
- A monthly cadence means the mirror's index is up to ~31 days (plus pull time) behind. That is acceptable only if *clients cannot be moved backwards* by it (section 2.1 on `full-sync`, section 2.3 on priority) and if the master remains reachable for anything newer (pull-through, section 6.2).
- Files the mirror lacks at request time (published after the last pull) are the common case, not the exception; a design where a missing file is a client-visible 404 is wrong for this cadence.

## 5. Two deployment patterns compared

- **Pattern A (recommended): index from the master only, packages from mirrors with pull-through to the master.** One baseurl, no index skew, no stale-index downgrades, mirrors untrusted, a mirror that is behind costs only extra redirects. Needs a redirector or Cloudflare rule in front of `packages/*`.
- **Pattern B: independent full mirrors, each with its own index.** Simple to host and to hand out as a URL, but every mirror is a different repository state, up to a month old, and mixing it with the master risks downgrades and priority blocking; it trusts the mirror operator completely.

## 6. Recommended mirror layout and sync protocol

### 6.1 Principles

1. **One logical repository per platform, one `baseurl`, one `identifier`, one `name`, one `priority`.** Never publish a mirror as a second repository.
2. **The index (`repo`, `repo.sha256`, `repo.info`) has exactly one source of truth: the master (R2, or the app).** Every host a client can get the index from serves the same generation. Mirrors carry `packages/` only for clients that use the single public base URL.
3. **Packages are append-only and addressed by name; name implies bytes.** A mirror compares SHA-256 from a manifest, never name or size.
4. **Order: files, then index, then pointer.** At the master and at every mirror.
5. **Untrusted mirrors are fine** under principle 2 because pkgman verifies each package against the checksum in the master's index (no signature exists, section 2.2).

### 6.2 Layout

Master on R2, per platform:

```
{platform}/packages/{file}.hpkg                immutable; Cache-Control: public, max-age=31536000, immutable; metadata sha256
{platform}/index/{generation}/repo             immutable copies of one index build
{platform}/index/{generation}/repo.info
{platform}/index/{generation}/repo.sha256
{platform}/repo, repo.info, repo.sha256        what pkgman requests; Cache-Control: no-cache; copied from the current generation
{platform}/manifest.json                       written last: {generation, builtAt, repoSha256, files:[{name, sha256, size}]}; Cache-Control: no-cache
```

`generation` is a monotonically increasing integer (or `builtAt` plus the repo hash) assigned by `RepoIndexer` when it uploads. Keep the last 3 to 6 generation directories (a HaikuPorts-sized `repo` is about 2.5 MB [W]); they let a mirror or an operator fetch a consistent set and let you roll the top-level files back by copying a generation. This is "immutable snapshots plus a pointer" for the *index* only; package files do not need snapshots because their names are immutable.

Public front end (Cloudflare Worker, a rule, or the existing app), host `ports.example.org`, repository base `https://ports.example.org/repo/{platform}`:
- `repo`, `repo.sha256`, `repo.info`: served from R2 directly (custom domain or proxy), never redirected to a mirror.
- `packages/{file}`: `302`/`303`/`307` (never 308) to the nearest mirror; each mirror answers 404-miss with a `302` to the R2 URL (pull-through), so a file the mirror has not pulled yet costs one extra hop and no failed install. If the redirector cannot know what a mirror has, this pull-through is the simplest correct rule. Alternative: the redirector reads each mirror's published `manifest.json` and redirects only for listed files.

Mirror host, per platform, static files (nginx or Caddy or an S3-compatible bucket), `Range` enabled, 404 for unknown names, no rate limiting that returns 429/503 to pkgman:

```
{platform}/packages/{file}.hpkg
{platform}/repo, repo.info, repo.sha256        only if the mirror is also used directly as a baseurl (pattern B)
{platform}/mirror-status.json                  {generation, syncedAt, repoSha256}
```

### 6.3 Sync protocol (run monthly, safe to run daily)

For each platform:
1. `GET manifest.json` from R2 (retry until it parses). If `generation` equals the local one, write `syncedAt` and stop. If the platform's manifest is absent (index deleted), do nothing.
2. For every `{name, sha256, size}` in the manifest: if `packages/{name}` exists locally, compare SHA-256 (cache by size+mtime between runs, but re-verify new or changed names); if equal skip; if different, treat as a name-reuse incident: download to a temp name, verify, replace atomically, and raise an alert (the master should never produce this). If missing: download to `packages/.tmp-{name}`, verify SHA-256 and size against the manifest, `rename` into place. Use ordinary HTTPS GET with retries and a bounded rate; the whole run must finish within the master's deletion grace period (72 h) and re-read the manifest if it restarts after more than that.
3. Only when every file is present and verified: if the mirror serves an index (pattern B), fetch `index/{generation}/repo` and `repo.info`, check `sha256(repo) == manifest.repoSha256`, write `repo.info`, then `repo` (each to a temp name and renamed), then `repo.sha256` last (this is the order `RepoIndexer.cs:299-303` uses and it minimises failed refreshes, section 2.5). Never touch the index before step 2 completes.
4. Write `mirror-status.json`.
5. Deletion: delete a package only if it is absent from the manifests of the last two generations *and* was last listed more than 30 to 60 days ago; do it after step 4 and never in the same run that changed the index. Keep a quarantine directory for a few days for anything deleted. Never delete on an absent or unparseable manifest.

### 6.4 Pattern B (independent mirrors users add directly)

If you want mirrors that users can add as their own baseurl (`pkgman add-repo https://mirror.example/repo/x86_64`), they must carry the whole set (steps 1 to 4) and serve their own index. Properties: consistent but up to a month stale, so users **must not** use more than one of {master, mirror} for the same repository, must not rely on `full-sync` against a lagging mirror after using the master (downgrades), and should be told that `identifier` and `name` are the same as the master's. The `add-repo` URL decides everything because it is the stored baseurl [T]. Mirrors keep their own `repo.info` byte-identical to the master's, including its `baseurl` line (it is ignored for downloads).

### 6.5 Staleness detection

- Master publishes `manifest.json` with `generation`, `builtAt` and `repoSha256`; the app's admin status page already reads `state.json` (`AdminEndpoints.cs:365`) and can show the same.
- Each mirror publishes `mirror-status.json`. A monitor (or the mirror's own cron) compares generations and alerts when a mirror is behind by more than the agreed lag (monthly cadence plus a few days), or when `sha256(repo) != repo.sha256` on any host.
- For pattern A, staleness of a mirror only costs pull-through traffic; for pattern B it costs freshness, so monitor it harder.
- Client-side detection does not exist; the only observable is `repo.sha256`.

### 6.6 What a client needs to do

Nothing, in pattern A: `pkgman add-repo https://ports.example.org/repo/{platform}`. Requirements on the client OS image: a libbnetapi with SSL if the public URL is https [T], otherwise provide http and do not enable "always redirect to https" at the CDN, because a 301 to https from http would fail on such a client [A]. Keep priority the same on any copy of the repository a user might also configure.

## 7. Required changes in RenkuPorts

In order of importance. (Items 1 to 3 should go into the R2 publisher work proposed in [r2-mirror.md](r2-mirror.md), if that project goes ahead.)

1. **Never reuse a file name for different bytes.** Add a persistent record of published names (platform, file name, sha256, first published at) that survives package deletion, supersede cleanup and reset (`ResetAsync` should keep it); `PackageStore` consults it and returns `Conflict` for a name previously published with another hash. The R2 publisher compares SHA-256 (object metadata or the manifest) before skipping an existing key. If an operator genuinely wants to reuse a name, it requires an explicit, logged override and a revision bump is the right answer instead.
2. **Publish a manifest and generation.** In `RepoIndexer.BuildLockedAsync` after the three index uploads: assign a generation, upload `index/{generation}/*`, then `manifest.json` last (files with SHA-256 and size; `state.json` already has the first, `Package.Size` has the second). R2 publish order: packages, per-generation index, top-level index in the existing order, `manifest.json`.
3. **Headers on R2 objects.** Immutable caching on packages, `no-cache` on index and manifest, no 429/5xx from edge rules for pkgman's user agent (`Services Kit (Haiku)` [T]).
4. **`Range` support on the Azure endpoint** (`PublicEndpoints.cs:63`: pass range processing to `Results.Stream`), as long as Azure serves packages at all. Also add `ETag` (the SHA-256) and an immutable `Cache-Control` to package responses.
5. **Fix `SupersededAsync`.** Group by package name (or exclude every name in the current `state.json` selection) so it cannot delete the file the live index lists or the only file of a name; consider deferring blob deletion by the same grace period the R2 publisher uses, so Azure and R2 behave alike. `Services/Cleanup.cs:121-127`.
6. **Newest-selection fallback.** In `BuildLockedAsync` fall back to the next older version when the newest of a name fails `CheckPackageAsync`, or at least surface "name dropped from the index" in the admin view; today a bad newest build silently removes the package from clients' view (`RepoIndexer.cs:48-52, 264-276`).
7. **Document the index-only triggers** and make `manifest.generation` change for them (already true if the generation is bumped on every upload).
8. **Redirector rules** (if not on Cloudflare): `packages/*` -> mirror with pull-through; index -> master; 302/303/307 only.
9. **Optional:** publish the generation and `builtAt` in `/api/platforms` so the web UI and monitors see staleness.

## 8. Open questions

1. **Is a stale-but-consistent pattern B mirror actually wanted, or is pattern A (index from master, packages from mirrors) acceptable?** It decides how much of section 6.3 step 3 is needed and whether `full-sync` downgrades are a concern.
2. **Does Cloudflare's cache in front of R2 cache `.hpkg` and the extensionless index files with the default rules?** Needs a test with `Cache-Status` headers; set explicit rules either way.
3. **Does R2 (public custom domain) answer 404 for missing keys and honour `Range` for pkgman's request shape?** Also check keys containing `~` and `+` through the chosen redirect, and the https/http decision for the OS images currently in the field [T: https failed on one box].
4. **Where does `repo.info`'s `baseurl` point after R2?** It is baked into `repo` (`RepoIndexer.cs:321`); if it changes, the `repo` and `repo.sha256` change for every client once. The current text uses `PublicBaseUrl` from configuration; decide on the final public host first.
5. **What does the Haiku project actually do for versioned build directories and `current`?** I found no public documentation; if needed, ask on the Haiku infrastructure channels before copying their scheme (the generation scheme above does not depend on it).
6. **Do any RenkuOS images or HaikuDepot builds read `repo.info` `baseurl` or the identifier for anything besides display?** Source shows only tools and preferences; HaikuDepot is not in this tree.
7. **Should the package checksum in the index and the server's `Package.Sha256` be cross-checked after every build?** `package_repo` computes the checksum from the file it reads; they should match by construction, and a mismatch would be a very expensive silent failure.
8. **Retention.** How many generations and how many days should the master keep unreferenced files? A longer grace costs only churn bytes; 7 to 14 days is proposed, 72 h is the minimum for a monthly pull that finishes in a day.
9. **`full-sync` downgrade behaviour with stale mirrors** is derived from libsolv's flags and not run. Worth a 10-minute test on a Haiku box with two index generations.
10. **Fork divergence.** Everything here was read in the RenkuOS fork; upstream Haiku may differ in `FetchFileJob` retry behaviour and the pkgman reinstall paths. The retry-forever behaviour of section 2.4 should be reproduced with a deliberate 503 before relying on it.
