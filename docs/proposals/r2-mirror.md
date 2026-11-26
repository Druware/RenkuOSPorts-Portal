# Cloudflare R2 publish mirror

**Status: Proposed, not implemented.** A prototype was built and reviewed on 2026-11-19: the build was clean and 117 of 117 tests passed against an in-memory bucket fake, but it was never run against real R2. The code was removed until the project is a go, and this document is meant to be enough to recreate it. The research behind it is in [mirrors-and-pkgman-versioning.md](mirrors-and-pkgman-versioning.md). Everything below is written as the proposed design; see "Implementation notes" for the code pieces and the risks that were never tested.

An optional copy of each platform's published pkgman repository in a Cloudflare R2 bucket, so package downloads could be served from R2 (no egress charge) instead of being streamed out of Azure Blob through the app. It would be off by default (`R2:Enabled=false`); with it off, nothing of it is registered and the server behaves exactly as before.

What it would be:

- A one-way mirror. Azure Blob and the SQL database remain the master of record: uploads, imports, verification, the index build (`RepoIndexer`) and the admin tools all keep working on Azure as before. The bucket is rebuilt from Azure and can be emptied or deleted at any time without losing anything.
- A static pkgman repository per platform. `pkgman add-repo <PublicBaseUrl>/<platform>` works against the bucket directly, and other mirrors can copy it.
- Optionally (`R2:RedirectDownloads=true`), the source of package downloads for clients that use the app's own URL: `GET /repo/<platform>/packages/<file>` would answer `302 Found` to the bucket.

What it would not be:

- Not a second place to upload to, and not a backup. Never write to the bucket by hand; the next sync overwrites or deletes what does not match Azure.
- Not used for the index files on the app's URL: `/repo/<platform>/repo`, `repo.info` and `repo.sha256` would always be served from Azure (they are small).
- Not a CDN in front of the app. Only the bucket's own public domain would serve from R2.

## Proposed design

A background service (proposed `Services/R2Mirror.cs`) would sync a platform right after `RepoIndexer` stored a new index for it, and every platform once an hour (and at startup), which also repairs drift in the bucket.

Bucket layout, per platform (`x86`, `x86_64`, `arm64`, `riscv64`):

| Key | Content | Cache-Control |
|---|---|---|
| `<platform>/packages/<fileName>` | the package, `Content-Type: application/octet-stream`, metadata `sha256` | `public, max-age=3600` |
| `<platform>/repo` | the index | `no-cache` |
| `<platform>/repo.info` | the repository info | `no-cache` |
| `<platform>/repo.sha256` | the index checksum | `no-cache` |

One sync of a platform would be:

1. Under the indexer's lock (`<platform>/index/lock`), read the index state (`state.json`) and the three index files from Azure, so they belong to one build; the upload itself runs outside that lock, under the separate `r2.lock` below, so a long upload never blocks the indexer. The packages mirrored are exactly the ones in `state.json`, the indexer's selected set. If the indexer is building, try again 5 s later.
2. For each package in that index: HEAD the object; if it is missing or its `sha256` metadata differs from the package's, stream it from Azure Blob into the bucket. A file name can come back with other bytes after its package row was deleted (superseded cleanup, admin delete, reset), so the comparison is by SHA-256, never by name or size.
3. Upload `repo`, then `repo.info`, then `repo.sha256` last. pkgman reads `repo.sha256` first, so the bucket never announces an index whose packages are not all there. If the platform has no index any more, the three files are deleted, `repo.sha256` first.
4. List `<platform>/packages/`. An object the published index names is never deleted. Any other object is recorded as absent (first sighting) and deleted only once it has been absent for `R2:DeleteGraceHours` (72 h by default), so clients and downstream mirrors with an older index can still fetch it.
5. Store the result in Azure at `<platform>/index/r2.json` (time, uploaded and deleted counts, error, the confirmed objects, and the absent-since times) and log the counts.

A lease on `<platform>/index/r2.lock` would keep two instances from syncing the same platform at once. The admin dashboard JSON (`GET /api/dashboard`) would get an `r2Mirror` entry per platform with the last sync time, counts and error (null when the mirror is off); the web UI would not show it at first.

The bucket would be written with an S3 client (`AWSSDK.S3`) configured for R2: region `auto`, path-style addressing, request and response checksums `WHEN_REQUIRED`, and an unsigned payload for the streamed `PutObject` (the package is streamed from Azure Blob, so it cannot be hashed up front). Object metadata `sha256` is what the HEAD comparison in step 2 reads. `Cache-Control` is `no-cache` on the three index files and `public, max-age=3600` on packages. The hour is deliberately not `immutable`: a file name can return with other bytes after its row was deleted, and an immutable year-long cache would pin the old bytes (see the caveats; the research doc proposes immutable caching only together with a persistent record of published names). `repo.info` is copied unchanged, so its embedded `baseurl` keeps pointing at the app.

Downloads with `R2:RedirectDownloads=true`: the app would redirect a package download only when the object is a confirmed member of the index the last sync published (from `r2.json`) and its SHA-256 equals the requested package's. Anything else (a package not in the index, such as an older version or a `_debuginfo` package, a package uploaded since the last sync, an instance that has not synced yet) would be streamed from Azure as before.

## Cloudflare setup

1. Create an R2 bucket (for example `renkuosports`). The location hint does not matter much; pick the one closest to most users.
2. Connect a custom domain to the bucket (bucket > Settings > Public access > Custom domains), for example `pkg.example.org`. Do not use the `r2.dev` development URL: it is rate limited and not meant for production. The domain's zone must be on the same Cloudflare account.
3. On that domain, add no rate limiting, WAF challenge, Bot Fight Mode or other bot rules for `/<platform>/*` (see the caveats: pkgman retries some error codes forever). Leave Cloudflare's compression settings alone; it does not compress `application/octet-stream`. Do not add a cache rule that overrides the origin's `Cache-Control` for the index files.
4. Create an R2 API token (R2 > Manage API tokens) with **Object Read & Write**, limited to this bucket. Note the Access Key ID and Secret Access Key (shown once), and the account's S3 endpoint `https://<accountid>.r2.cloudflarestorage.com`.
5. For each downstream mirror, create a separate token with **Object Read only** for this bucket.

## Proposed configuration

| Config key | App Service setting | Value |
|---|---|---|
| `R2:Enabled` | `R2__Enabled` | `true` to run the sync; default `false` |
| `R2:ServiceUrl` | `R2__ServiceUrl` | `https://<accountid>.r2.cloudflarestorage.com` |
| `R2:Bucket` | `R2__Bucket` | the bucket name |
| `R2:AccessKeyId` | `R2__AccessKeyId` | Key Vault reference |
| `R2:SecretAccessKey` | `R2__SecretAccessKey` | Key Vault reference |
| `R2:PublicBaseUrl` | `R2__PublicBaseUrl` | the bucket's custom domain, `https://pkg.example.org` |
| `R2:RedirectDownloads` | `R2__RedirectDownloads` | `true` to redirect package downloads to the bucket; default `false` |
| `R2:DeleteGraceHours` | `R2__DeleteGraceHours` | hours an object stays after it left the index; default `72` |

They would be set as App Service application settings (and mapped into the api sidecar like every other key, see [azure-deployment.md](../azure-deployment.md)), with the two keys as Key Vault references (`@Microsoft.KeyVault(SecretUri=...)`). Real values must never go into `appsettings.json` or any other committed file; it would only carry empty defaults. Locally, use user secrets or environment variables. When `R2:Enabled` is true the app would refuse to start if any of `ServiceUrl`, `Bucket`, `AccessKeyId`, `SecretAccessKey` or `PublicBaseUrl` is missing, or if either URL is not absolute `https`.

## Proposed rollout

1. **Sync only.** Set the settings with `R2:Enabled=true` and `R2:RedirectDownloads=false`, restart. Watch the log for `<platform> R2 mirror: N packages in the index, N uploaded, 0 deleted` (the first sync uploads everything; that is the Azure egress bill, see Costs) and the `r2Mirror` entry of `GET /api/dashboard`. Check the bucket: `<platform>/repo.sha256` exists, `curl https://pkg.example.org/<platform>/repo | sha256sum` matches it, and the package count matches the index (the admin dashboard's index package count).
2. **Test pkgman against the bucket.** On a Haiku test box with HTTPS-capable pkgman (see caveats): `pkgman add-repo https://pkg.example.org/<platform>`, `pkgman refresh`, install and update a few packages. Do not keep both this and the app's repository URL added on one box: the two have the same name and identifier. Remove it again with `pkgman drop-repo` when done.
3. **Redirect downloads.** Set `R2:RedirectDownloads=true`, restart. `curl -sI https://<app>/repo/<platform>/packages/<file>` should answer `302` with `Location: https://pkg.example.org/<platform>/packages/<file>`; then run an update on a test box that uses the app's URL. Clients keep the app's URL; nothing changes for them except where the bytes come from.

Rollback at any phase would be: set `R2:RedirectDownloads=false` (downloads stream from Azure again at once), and `R2:Enabled=false` to stop syncing. The bucket can stay as it is or be deleted; Azure never depended on it. Clients that added the bucket URL directly keep using it, so leave the bucket in place until they have switched back.

## Costs

- R2 storage: $0.015 per GB-month (the first 10 GB-month free). Operations: writes and lists are Class A ($4.50 per million), HEADs and reads Class B ($0.36 per million), with a monthly free tier. The hourly reconcile does one HEAD per index package per platform, about 720 per package a month, which is cents for a few thousand packages. Downloads that Cloudflare's cache answers cost no operation.
- R2 egress: $0. This is the point of the mirror.
- Azure egress for the uploads to R2: the sync streams each new package out of Azure Blob, billed as Azure internet egress (about $0.087 per GB). The first sync pays it for the whole published set; later syncs only for new packages, plus re-uploads if the bucket was changed or emptied. The index files are re-uploaded on every sync (small).
- Check current prices on Cloudflare's and Azure's pricing pages before planning around these numbers.

## Caveats

- **302, never 308.** pkgman follows 301, 302, 303 and 307 across hosts but not 308. The app would answer 302.
- **No Content-Encoding on packages.** pkgman sends `Accept-Encoding: gzip`; a `.hpkg` must be served as is. The mirror must never set `Content-Encoding`, and Cloudflare does not compress `application/octet-stream`; keep it that way (no transform rules).
- **Errors pkgman retries forever.** pkgman fails once on 401, 403, 404, 405, 406, 416, 501 and 502, but retries every other 4xx and 5xx (429, 410, 500, 503 and so on) immediately and forever, with no back-off. So: no rate limiting, WAF challenge or bot rule on the bucket's domain for `/<platform>/*`, and a missing object must answer 404 (R2 does), never 5xx. A 403 on the redirect target fails once with a clear error, so a bucket that is not public shows up quickly.
- **Range.** pkgman resumes a partial download with a `Range` request and appends whatever comes back. R2 honours `Range` (206), so the redirect path resumes correctly.
- **HTTPS in pkgman.** The bucket's domain is HTTPS only. RenkuOS Haiku builds before Source commit 441b121c25 have no HTTPS in pkgman; they cannot use the bucket URL, and with `RedirectDownloads` on they cannot download packages through the app either (the redirect target is HTTPS). Test boxes need a fixed build, and redirects should wait until the clients in the field have one.
- **repo.info points at the app.** The `repo` file embeds the repository info, whose `baseurl` is `RepoIndex:PublicBaseUrl/repo/<platform>` (the app), and the mirror would copy it unchanged. That is fine: `pkgman add-repo <url>` stores the URL the user typed as the base URL, so clients that added the bucket fetch packages from the bucket.
- **Edge cache after a file name is reused.** Package objects are cached for an hour, not forever, because a file name can return with other bytes after its row was deleted. In that hour Cloudflare may still serve the old bytes; pkgman then fails the checksum. Purge the URL in the Cloudflare dashboard if it matters.
- **One `PutObject` per package.** shortcut: packages would be uploaded in a single request, which R2 limits to just under 5 GiB; a larger package would need a multipart upload.

## Downstream static mirrors

A downstream mirror could copy the bucket with any S3 tool, using a read-only token. With rclone (remote `r2`, `provider = Cloudflare`, `endpoint = https://<accountid>.r2.cloudflarestorage.com`), per platform, in this order:

```sh
P=arm64; SRC=r2:renkuosports/$P; DST=/srv/mirror/$P
rclone copy "$SRC/packages" "$DST/packages"                  # 1. new packages, nothing deleted yet
rclone copyto "$SRC/repo" "$DST/repo.new"                    # 2. the index, under temporary names
rclone copyto "$SRC/repo.info" "$DST/repo.info.new"
rclone copyto "$SRC/repo.sha256" "$DST/repo.sha256.new"
# 3. a sync may have run between the copies: if the pair does not match, stop and run again
[ "$(sha256sum < "$DST/repo.new" | cut -d' ' -f1)" = "$(tr -d '[:space:]' < "$DST/repo.sha256.new")" ] || exit 1
mv "$DST/repo.new" "$DST/repo"; mv "$DST/repo.info.new" "$DST/repo.info"
mv "$DST/repo.sha256.new" "$DST/repo.sha256"                 # 4. repo.sha256 last of all
rclone sync "$SRC/packages" "$DST/packages"                  # 5. now drop what the bucket dropped
```

Packages first, then `repo` and `repo.info`, `repo.sha256` last of all, as the publisher itself does, so the mirror's clients never see an index naming packages it does not have yet. Step 5 only removes objects the bucket already deleted, which happens at least `R2:DeleteGraceHours` after they left the index; run the mirror well within that period (hourly or daily against the 72 h default), or it may serve an index naming packages that are gone. Serve the result as static files with `Range` support, 404 for missing files, the same `Cache-Control` as the bucket, and none of the error codes pkgman retries forever.

## Implementation notes

The prototype consisted of the pieces below; names and hook points are what to recreate.

- `Services/R2Mirror.cs`: the background service described above (sync after an index build, hourly reconcile, startup sync, `r2.lock`, `r2.json`).
- `R2Options`: the `R2:*` settings above, bound from configuration and validated at startup (all five required values present when enabled, both URLs absolute `https`).
- `IObjectStore` and `S3ObjectStore`: a small bucket abstraction (HEAD with metadata, streamed put with metadata and `Cache-Control`, list by prefix, delete) over `AWSSDK.S3` with the client settings above; the interface is what let the tests use an in-memory fake bucket.
- `RepoIndexer.IndexBuilt`: an event raised after the indexer stored a new index for a platform; `R2Mirror` subscribes to it to sync that platform.
- `TryLeaseAsync`: a try-acquire for the blob lease, used for `r2.lock` so a second instance skips a platform instead of waiting.
- `Endpoints/PublicEndpoints.cs`: the 302 hook in the package download route, active only when `R2:RedirectDownloads` is on.
- The admin dashboard endpoint (`GET /api/dashboard`): the `r2Mirror` entry per platform.
- Dependency: `AWSSDK.S3`.
- Registration: when `R2:Enabled` is false none of the above is registered.

Test cases that existed (against the in-memory bucket fake): disabled means nothing is registered; upload order is packages, `repo`, `repo.info`, `repo.sha256` last; re-upload on sha256 mismatch or missing metadata; objects that are index members are never deleted; the grace boundary (not deleted before `R2:DeleteGraceHours`, deleted after); when the index is gone the index files are removed with `repo.sha256` first; back off and retry when the indexer holds its lock; the redirect conditions (only confirmed index members with a matching sha256, otherwise stream from Azure); options validation; the dashboard entry.

Untested risks, to check first against a real bucket (see also open question 3 in the research doc):

- A real R2 accepting the unsigned streaming `PutObject` (checksums `WHEN_REQUIRED`, unsigned payload).
- HEAD on a missing key answering 404 as the sync expects.
- Listing `<platform>/packages/` across more than one page of results.
- Whether a `+` in a file name survives R2's public URL path and the 302 `Location` (file names with `~` and `+` pass through redirects unencoded).
