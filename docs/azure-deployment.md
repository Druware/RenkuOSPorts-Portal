# Deploying RenkuOSPorts-Public to Azure

This is a plan, not a record of a deployment: nothing here has been run. Names, regions and SKUs are placeholders. Prices are rough estimates for a small setup and must be checked in the Azure pricing calculator for your region. The server code now covers the main Azure needs (see "What the code does for Azure"); the managed identity and Application Insights paths have not been tested against real Azure.

## Target layout

| Need | Azure resource | Notes |
|---|---|---|
| Container | Resource group `rg-renkuports` | Everything below lives in one group. |
| App (`web` main container + `api` sidecar) | One Web App for Containers on the App Service plan (Linux), sidecar layout. Main container `web`: `<ACR_LOGIN_SERVER>/renkuos-ports/web` (nginx, port 8080, `WEBSITES_PORT=8080`) | The only public entry point: serves the SPA and reverse-proxies `/api/`, `/signin-github` and the pkgman repository paths to the API on `localhost:8081`. Holds no secrets. |
| API sidecar (`api`) | Sitecontainer of the same app: `<ACR_LOGIN_SERVER>/renkuos-ports/api` (Kestrel, `ASPNETCORE_HTTP_PORTS=8081` in Azure) | No ingress; only nginx reaches it, over `localhost`. Holds all the app settings and uses the app's managed identity. |
| Image registry | Azure Container Registry | Basic SKU is enough. CI (`.github/workflows/ci.yml`, job `push-images`) pushes `<ACR_LOGIN_SERVER>/renkuos-ports/web` and `<ACR_LOGIN_SERVER>/renkuos-ports/api`, each tagged with the commit SHA and `latest`. |
| Database | Azure SQL Database | Start with Basic or Standard S0 (see below). |
| Packages and logs | Storage account (StorageV2, LRS) with private container `packages` | The app creates the container at startup if the identity or key allows it. |
| Email | Azure Communication Services + Email Communication Service + a verified domain | Used by the `AzureCommunication` notifier. |
| Secrets | Key Vault, read by the app's managed identity | App settings use Key Vault references. |
| Telemetry | Application Insights (workspace-based) | Enabled by setting `APPLICATIONINSIGHTS_CONNECTION_STRING` (OpenTelemetry Azure Monitor). |

### Container or code deploy

Recommendation: one Web App for Containers (Linux) on the plan, using sidecar containers, both images pulled from ACR with the app's managed identity (`AcrPull`). The main container is the nginx image (`nginx/Dockerfile`), which carries the built Vue SPA and proxies the API paths; it is the only container that gets ingress (`WEBSITES_PORT=8080`). The api image (root `Dockerfile`, the .NET server on Kestrel, no SPA) runs as a sidecar in the same app. Reasons: the recipe sync shells out to `git` and the repository index uses Haiku's `package_repo`, which the API image installs or builds and the stock App Service Linux .NET image does not provide; and the images that run locally in compose are the ones that run in Azure. A code deploy (`dotnet publish` zip, runtime `DOTNET|10.0`) has no `git` or `package_repo`. The cost of the container route is an ACR (about 5 USD per month) and two image pushes per deploy.

The containers share the app's network namespace and reach each other on `localhost`, so the api must not use nginx's 8080. The api image defaults to `ASPNETCORE_HTTP_PORTS=8080`; the image is not changed, and in Azure the api container gets `ASPNETCORE_HTTP_PORTS=8081`. On nginx, set `API_UPSTREAM_HOST=localhost`, `API_UPSTREAM_PORT=8081`, `API_UPSTREAM_SCHEME=http`; the nginx image defaults (`api`, 8080, `http`) stay as they are for compose. The nginx container needs no secrets; the api sidecar needs all the other settings and uses the app's managed identity.

### Database tier

Standard S0 (10 DTU) at roughly 15 USD per month is a safe starting point; Basic (5 DTU, about 5 USD) works for a trial. The app uses EF retry-on-failure, but a serverless database that auto-pauses may still outlast the retries during startup migrations, so prefer a non-pausing tier. The job-claim query uses `UPDLOCK, READPAST` hints, which Azure SQL supports.

### Storage and downloads

Keep the container private and keep proxying downloads through the app, as the code does today (`GET /repo/{platform}/packages/{file}` streams the blob). That keeps the URL stable and lets the app control access, at the cost of all download bandwidth going through the App Service plan. Making the container public-read and redirecting to blob URLs would offload that but changes the URLs and needs a code change. If download traffic grows, put Azure Front Door (Standard) or the Azure CDN in front of `/repo/*` with caching, which needs no code change except cache headers. Front Door adds its own request size and timeout limits to the API path, so route only `/repo/*` and the SPA through it, not `/api/client/*` uploads.

### Email

Create an Email Communication Service, add either the Azure managed domain (quickest, sender `DoNotReply@<guid>.azurecomm.net`) or a custom domain with the SPF, DKIM and DKIM2 DNS records verified, then connect the domain to an Azure Communication Services resource. Set `Email__From` to a sender address that exists on the connected domain. The app authenticates to ACS with the managed identity (`Email__AzureCommunication__Endpoint`) or, as a fallback, the connection string.

## Configuration map

All settings in this section are app settings on the single app. The app reads configuration through `IConfiguration`, so each key maps to an App Service application setting with `:` replaced by `__`. Secrets use Key Vault references of the form `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<name>/)`, resolved through the app's system-assigned managed identity (grant it the `Key Vault Secrets User` role); see "How the sidecar gets its settings" for how they reach the api container. Use App Settings, not the portal's "Connection strings" blade, because the blade adds a type prefix to the variable name.

| Config key | App Service setting | Value source |
|---|---|---|
| `ConnectionStrings:Sql` | `ConnectionStrings__Sql` | Key Vault secret, or a plain setting if it uses managed identity (no secret in it) |
| `ConnectionStrings:Blob` | `ConnectionStrings__Blob` | Fallback only: Key Vault secret (storage connection string); not needed when `Blob__ServiceUri` is set |
| `Blob:ServiceUri` | `Blob__ServiceUri` | Plain setting, `https://<SA>.blob.core.windows.net`. Uses `DefaultAzureCredential` (the app's managed identity, needs Storage Blob Data Contributor); wins over the connection string |
| `Blob:Container` | `Blob__Container` | Plain setting, `packages` |
| `Email:Mode` | `Email__Mode` | Plain setting, `AzureCommunication` |
| `Email:From` | `Email__From` | Plain setting, a sender address on the verified domain |
| `Email:AzureCommunication:Endpoint` | `Email__AzureCommunication__Endpoint` | Plain setting, `https://<ACS>.<region>.communication.azure.com`. Uses `DefaultAzureCredential`; the identity needs a suitable ACS role (verify which, e.g. Communication and Email Service Owner or a custom role allowing send); wins over the connection string |
| `Email:AzureCommunication:ConnectionString` | `Email__AzureCommunication__ConnectionString` | Fallback only: Key Vault secret (ACS connection string) |
| `Email:Smtp:Host`, `Email:Smtp:Port` | not set | Unused when `Email:Mode` is `AzureCommunication` |
| `Jwt:Key` | `Jwt__Key` | Key Vault secret, at least 32 random characters; changing it invalidates issued client tokens (they last 12 hours, clients fetch a new one on a 401) |
| `Auth:GitHub:ClientId` | `Auth__GitHub__ClientId` | Plain setting |
| `Auth:GitHub:ClientSecret` | `Auth__GitHub__ClientSecret` | Key Vault secret |
| `PortsTree:Path` | `PortsTree__Path` | Plain setting, a path under `/home`, e.g. `/home/ports`, so the clone survives restarts (needs `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true`) |
| `PortsTree:GitUrl` | `PortsTree__GitUrl` | Plain setting, the ports repository URL (must be readable without credentials, or embed a token via Key Vault) |
| `PortsTree:Branch` | `PortsTree__Branch` | Plain setting, optional, default `main` |
| `Database:MigrateOnStartup` | `Database__MigrateOnStartup` | Plain setting, default true; `false` when migrations are applied by the pipeline |
| `Jobs:LeaseHours` | `Jobs__LeaseHours` | Plain setting, optional, default 72 |
| `RepoIndex:PublicBaseUrl` | `RepoIndex__PublicBaseUrl` | Plain setting, `https://<APP>.azurewebsites.net` or the custom domain (the app's public host); it is the base URL in each platform's `repo.info`, so pkgman fetches from it. Other `RepoIndex` keys are optional (see the README) |
| `Seed:AdminEmail` | `Seed__AdminEmail` | Plain setting |
| `Seed:AdminPassword` | `Seed__AdminPassword` | Key Vault secret; read only when the admin account does not exist yet, so it can be removed after first start |

Platform settings, not read by the app code. All are app settings on the single app:

| Setting | Value | Why |
|---|---|---|
| `WEBSITES_PORT` | `8080` | The port of the main container (nginx), the only one with ingress. |
| `ASPNETCORE_HTTP_PORTS` | `8081` | The api image defaults to 8080 (root `Dockerfile`), which nginx already holds: the containers share the app's network namespace. The image is unchanged; the sitecontainer environment overrides it. Also the sitecontainer's `targetPort`. |
| `WEBSITES_ENABLE_APP_SERVICE_STORAGE` | `true` | Persists `/home`, where the ports checkout lives (`PortsTree__Path`). Whether the api sidecar sees `/home` is unverified (see "How the sidecar gets its settings"). |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | from the Application Insights resource (Key Vault reference or plain) | When set, the app sends telemetry through OpenTelemetry (Azure Monitor); when unset, no telemetry export. |
| `ASPNETCORE_ENVIRONMENT` | leave unset (Production) | `appsettings.Development.json` carries the local SA password and JWT key and must not load in Azure. |
| `API_UPSTREAM_HOST` | `localhost` | Where nginx finds the api. Validated at container start (a bad value stops the container). The image default is `api` (compose). |
| `API_UPSTREAM_PORT` | `8081` | The api's port in Azure. The image default is 8080 (compose). |
| `API_UPSTREAM_SCHEME` | `http` | Inside the app's network namespace there is no TLS. This is also the image default; set it explicitly for clarity. |

`ASPNETCORE_FORWARDEDHEADERS_ENABLED` is not needed and should not be set: `Program.cs` calls `UseForwardedHeaders` itself (see "Why trusting forwarded headers is safe here").

nginx resolves `API_UPSTREAM_HOST` once, when it starts. `localhost` always resolves, so there is no restart-on-address-change concern. If the api sidecar is not listening yet (it starts in parallel, and its first start runs migrations and may take a while), nginx starts anyway and returns 502 for `/api/`, `/signin-github` and the repository paths until the api is up. There is no start-order dependency between the containers that this guide relies on.

### How the sidecar gets its settings

From `az webapp sitecontainers create -h`: the spec file gives each sitecontainer its own `environmentVariables` list of `{ "name", "value" }`, and the help example sets `"value": "APPSETTING_NAME1"`, that is, the value is the name of an app setting. So each sitecontainer receives explicit environment variables mapped from app settings; this guide maps every key in the configuration map by the same name (see the spec file in the steps). The create and update commands have no flag for environment variables, so the spec file (`--sitecontainers-spec-file`) or the portal (Deployment Center > Containers) is the only way to set them. Unverified: whether a sidecar also inherits all app settings without a mapping, and whether the main container does (the explicit mapping makes the answer irrelevant, and mapping the same names is harmless either way). Also unverified: that a mapped setting whose value is a Key Vault reference arrives resolved (the platform resolves references at app-setting level, so it should), and how `/home` persistence (`WEBSITES_ENABLE_APP_SERVICE_STORAGE`, `PortsTree__Path`) applies to a sidecar: the spec also supports `volumeMounts` (`containerMountPath`, `volumeSubPath`, `readOnly`), and the portal may be needed. If `/home` is not shared, the ports clone lands in the container's writable layer and is re-cloned after a restart.

Key Vault references and the managed identity: the references are app-level setting values, resolved by the platform with the app's identity (the app's system-assigned identity, which needs `Key Vault Secrets User`); the container only ever sees the resolved value. The api sidecar's own calls to SQL, Blob and ACS through `DefaultAzureCredential` use the same app identity (one identity per app, shared by both containers; whether the managed identity endpoint is reachable from a sidecar is unverified). The nginx container needs no secrets: it uses only the three `API_UPSTREAM_*` settings.

## Why trusting forwarded headers is safe here

`Program.cs` trusts `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from any hop (`ForwardedHeadersOptions` with `KnownProxies` and `KnownIPNetworks` cleared), because the sender's address cannot be known in advance on App Service. That is safe here because the api listens only inside the app's network namespace (port 8081), nginx is its only caller over `localhost`, and the sidecar gets no ingress: the only way in from outside is the main container on 8080, and nginx overwrites those headers before proxying. Do not expose the api port directly (making the api the main container, or setting `WEBSITES_PORT` or the main container's target port to 8081): anyone could then send those headers themselves and spoof the scheme, the host (which the GitHub OAuth redirect URI is built from) and the client IP (which the API's logs see). That the platform really gives a non-main sitecontainer no ingress is documented behavior as understood here, but has not been tested: check that `https://<APP>.azurewebsites.net/` is served by nginx and that no other hostname or port reaches Kestrel.

## What the code does for Azure

- **Uploads.** The package endpoint accepts up to 4 GiB. The client sends `sha256` and `buildDate` form fields before the `file` part; the server streams the body through a SHA-256 hash straight into blob `{platform}/{sha256}/{fileName}` with no temp file, so there is no disk-space requirement and no hashing or copy pause after the body ends, and it rejects the upload if the claimed sha256 differs from the received bytes. Locally a 1 GB upload took 18 s with flat `/tmp` and about 320 MiB of memory. The response now comes right after the last byte, so the roughly 230 s front-end idle timeout is no longer a concern for the post-upload work. Still verify on App Service that a multi-GB upload passes end to end, and do not route `/api/client/*` through Front Door or Application Gateway without checking their request size limits. Enable Always On (Basic tier or higher) on the app. nginx streams upload bodies to the API without buffering (`proxy_request_buffering off`, `client_max_body_size 4097m` on the package upload path).
- **Ports tree.** With `PortsTree:GitUrl` set, the admin recipe sync clones the repository (blobless, `--branch` `PortsTree:Branch`, default `main`) into `PortsTree:Path`, or fetches and hard-resets it on later syncs, then scans it. Use a path under `/home`. The first clone of a large repository may exceed the front-end timeout of about 230 s; do the first sync once and, if the browser request times out, wait and retry (the server-side work may keep running; a second request while it runs gets 409). The lock is per instance.
- **Automatic updates.** The scheduled auto-update (admin page, Recipes tab) fetches new commits itself only with `PortsTree:GitUrl` set; without it the tree never changes on App Service, so set `PortsTree:GitUrl` before enabling it.
- **Data Protection keys** are stored at blob `dataprotection/keys.xml` in the packages container, so restarts, deploys and extra instances keep sign-in cookies valid. (They are unencrypted at rest beyond storage encryption; restrict the container to the app's identity.)
- **Managed identity** for Blob (`Blob:ServiceUri`) and ACS email (`Email:AzureCommunication:Endpoint`) via `DefaultAzureCredential`; connection strings remain a fallback. Untested against real Azure.
- **SQL.** `EnableRetryOnFailure` is on. Edge case: a transient error after the job-claim UPDATE commits can make the retry claim a second job; the stray job waits out its lease (default 72 h) or can be requeued. SQL managed identity works through the connection string (`Authentication=Active Directory Default` or `Active Directory Managed Identity`, SqlClient 6.1.7); the app identity needs a contained database user with `db_datareader`, `db_datawriter` and, while it migrates at startup, `db_ddladmin`.
- **Migrations.** Applied at startup unless `Database:MigrateOnStartup=false`. For a pipeline step generate a script with `dotnet ef migrations script --idempotent -p src/RenkuPorts.Server -o migrate.sql` and apply it with `sqlcmd` or `az sql` using an Entra identity before deploying. With several instances and startup migration enabled they can race; use the pipeline route before scaling out.
- **Telemetry.** Setting `APPLICATIONINSIGHTS_CONNECTION_STRING` enables Azure Monitor OpenTelemetry. Untested against real Azure.
- **pkgman repository index.** A background service builds `repo`, `repo.info` and `repo.sha256` per platform into blob `<platform>/index/` with Haiku's `package_repo`, which the image builds from source for its own architecture (amd64 under `az acr build`). Instances coordinate through a blob lease per platform, so scaling out is safe. Index temp files (new packages only, after the first build) go to the container's temp directory; the first build of a platform downloads all of its newest packages there once.
- **Repository import.** The admin Import tab copies missing packages from another pkgman repository. The source URL must be reachable from the App Service (a repository on a private LAN or a VM host-only address is not; publish it, or run the import from a local instance against the same SQL and storage). Each file is downloaded to the container's temp directory, checked, then streamed to blob, four at a time, so temp space must hold four of the largest packages. An import runs inside the instance that started it; if that instance restarts, run the import again and it continues with what is still missing. Long imports keep running after the request returns, so the ~230 s front-end timeout does not apply.
- **Forwarded headers** are applied by `UseForwardedHeaders` in `Program.cs` and trusted from any hop; nginx overwrites `X-Forwarded-For`/`Proto` and sets `X-Forwarded-Host` to the public host. That is safe only because of the sidecar layout (see "Why trusting forwarded headers is safe here").

## Remaining issues

1. Downloads are streamed through the app without range support, so large downloads occupy a worker and restart from zero if interrupted. Front Door or a public-read container would be the way out.
2. `appsettings.Development.json` holds the local SA password and JWT key; keep `ASPNETCORE_ENVIRONMENT` unset (Production) in Azure.
3. `AllowedHosts` is `*`; tighten to the real host name if desired.
4. The sync lock is per instance; run one instance, or accept that two syncs on different instances can overlap.

## Database permissions

The app signs in to Azure SQL as the Web App's system-assigned managed identity, so it needs a contained user in the app database. Connect to the app database (not `master`) as the server's Entra admin, for example with the portal's Query editor or `sqlcmd --authentication-method ActiveDirectoryDefault` after `az login`, and run the following. The user name is the Web App's name, or `<app name>/slots/<slot>` for a deployment slot.

```sql
CREATE USER [<app name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<app name>];
ALTER ROLE db_datawriter ADD MEMBER [<app name>];
ALTER ROLE db_ddladmin  ADD MEMBER [<app name>];  -- only while Database:MigrateOnStartup is true

-- check
SELECT p.name, r.name AS role
FROM sys.database_role_members m
JOIN sys.database_principals p ON p.principal_id = m.member_principal_id
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
WHERE p.name = '<app name>';
```

`db_owner` is not needed. The migrations create and alter tables, indexes and foreign keys, which `db_ddladmin` covers, and their only raw SQL is `UPDATE` and `DELETE`, which `db_datawriter` covers. The database must exist before the app starts (`az sql db create` below), because creating it would need `dbmanager` in `master`.

To keep DDL rights away from the running app, set `Database__MigrateOnStartup=false`, apply migrations from the pipeline with an identity that has the same three roles (see "Migrations" above), and drop the role from the app:

```sql
ALTER ROLE db_ddladmin DROP MEMBER [<app name>];
```

## Step-by-step with the Azure CLI

Placeholders in angle brackets. Run these yourself after reviewing; they are not executed by anything in this repo. Resource names for ACR, storage, Key Vault, SQL server and the app must be globally unique. The sitecontainers commands need a recent Azure CLI (the `az webapp sitecontainers` group exists in 2.81.0; the flags below were copied from its `-h` output).

    # variables
    RG=rg-renkuports; LOC=<region, e.g. westeurope>
    ACR=<uniqueacrname>; PLAN=plan-renkuports; APP=<unique-app-name>
    SA=<uniquestorageacct>; KV=<unique-kv-name>; SQLS=<unique-sql-server>; DB=RenkuPorts
    ACS=<acs-name>; EMS=<ems-name>; LAW=log-renkuports; AI=appi-renkuports

    az group create -n $RG -l $LOC

    # registry
    az acr create -g $RG -n $ACR --sku Basic

    # app service plan and ONE sitecontainers web app (no image yet; the containers are added below)
    az appservice plan create -g $RG -n $PLAN --is-linux --sku B2
    az webapp create -g $RG -p $PLAN -n $APP --sitecontainers-app
    az webapp identity assign -g $RG -n $APP
    az webapp config set -g $RG -n $APP --always-on true
    PRINCIPAL=$(az webapp identity show -g $RG -n $APP --query principalId -o tsv)   # the one identity for both containers
    az role assignment create --assignee $PRINCIPAL --role AcrPull --scope $(az acr show -n $ACR --query id -o tsv)

    # storage
    az storage account create -g $RG -n $SA --sku Standard_LRS --kind StorageV2 --allow-blob-public-access false
    az storage container create --account-name $SA -n packages --auth-mode login
    az role assignment create --assignee $PRINCIPAL --role "Storage Blob Data Contributor" --scope $(az storage account show -g $RG -n $SA --query id -o tsv)

    # sql (Entra admin = you or a group; SQL auth off if your policy allows)
    az sql server create -g $RG -n $SQLS -l $LOC --enable-ad-only-auth --external-admin-principal-type User \
      --external-admin-name <your-entra-user> --external-admin-sid <your-object-id>
    az sql db create -g $RG -s $SQLS -n $DB --edition Standard --service-objective S0
    az sql server firewall-rule create -g $RG -s $SQLS -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
    # then grant the app's identity access to $DB: see "Database permissions" above

    # email
    az communication create -g $RG -n $ACS --location global --data-location <e.g. Europe>
    az communication email create -g $RG -n $EMS --location global --data-location <e.g. Europe>
    az communication email domain create -g $RG --email-service-name $EMS --name AzureManagedDomain \
      --location global --domain-management AzureManaged
    # link the domain to $ACS (portal: Communication Service > Email > Domains, or az communication update --linked-domains)
    # grant $PRINCIPAL a role on $ACS that allows sending email (verify the role), and use Email__AzureCommunication__Endpoint below.
    # Fallback if managed identity does not work: ACS_CONN=$(az communication list-key -g $RG -n $ACS --query primaryConnectionString -o tsv)

    # key vault (RBAC) and secrets
    az keyvault create -g $RG -n $KV --enable-rbac-authorization true
    az role assignment create --assignee $PRINCIPAL --role "Key Vault Secrets User" --scope $(az keyvault show -n $KV --query id -o tsv)
    # you also need Key Vault Secrets Officer to write secrets
    az keyvault secret set --vault-name $KV -n jwt-key --value "$(openssl rand -base64 48)"
    az keyvault secret set --vault-name $KV -n github-secret --value "<github oauth client secret>"
    az keyvault secret set --vault-name $KV -n admin-password --value "<strong password>"

    # telemetry
    az monitor log-analytics workspace create -g $RG -n $LAW
    az monitor app-insights component create -g $RG -a $AI -l $LOC --workspace $LAW

    # images first (from the repo root), because the containers below need an image to pull.
    # CI pushes both images on every v* tag; for the very first deploy you can use ACR tasks:
    az acr build -r $ACR -t renkuos-ports/api:1 .
    az acr build -r $ACR -t renkuos-ports/web:1 -f nginx/Dockerfile .

    # app settings (app level). The api's settings and the nginx upstream settings live together; see
    # "How the sidecar gets its settings" for how the api container receives them.
    KVU=https://$KV.vault.azure.net/secrets
    az webapp config appsettings set -g $RG -n $APP --settings \
      WEBSITES_PORT=8080 \
      ASPNETCORE_HTTP_PORTS=8081 \
      API_UPSTREAM_HOST=localhost API_UPSTREAM_PORT=8081 API_UPSTREAM_SCHEME=http \
      "ConnectionStrings__Sql=Server=tcp:$SQLS.database.windows.net,1433;Database=$DB;Authentication=Active Directory Managed Identity;Encrypt=True" \
      Blob__ServiceUri=https://$SA.blob.core.windows.net Blob__Container=packages \
      Email__Mode=AzureCommunication Email__From=<DoNotReply@yourdomain> \
      Email__AzureCommunication__Endpoint=<https://ACS-name.region.communication.azure.com> \
      "Jwt__Key=@Microsoft.KeyVault(SecretUri=$KVU/jwt-key/)" \
      Auth__GitHub__ClientId=<github client id> \
      "Auth__GitHub__ClientSecret=@Microsoft.KeyVault(SecretUri=$KVU/github-secret/)" \
      Seed__AdminEmail=<admin email> "Seed__AdminPassword=@Microsoft.KeyVault(SecretUri=$KVU/admin-password/)" \
      WEBSITES_ENABLE_APP_SERVICE_STORAGE=true PortsTree__Path=/home/ports \
      PortsTree__GitUrl=<ports repo URL> PortsTree__Branch=main \
      RepoIndex__PublicBaseUrl=https://$APP.azurewebsites.net \
      APPLICATIONINSIGHTS_CONNECTION_STRING=<from the App Insights resource>

    # the two containers, from a spec file (the CLI has no flag for environment variables, so the spec file is needed)
    cat > sitecontainers.json <<EOF
    [
      { "name": "web",
        "properties": {
          "image": "$ACR.azurecr.io/renkuos-ports/web:1", "targetPort": "8080", "isMain": true, "authType": "SystemIdentity",
          "environmentVariables": [
            { "name": "API_UPSTREAM_HOST",   "value": "API_UPSTREAM_HOST" },
            { "name": "API_UPSTREAM_PORT",   "value": "API_UPSTREAM_PORT" },
            { "name": "API_UPSTREAM_SCHEME", "value": "API_UPSTREAM_SCHEME" } ] } },
      { "name": "api",
        "properties": {
          "image": "$ACR.azurecr.io/renkuos-ports/api:1", "targetPort": "8081", "isMain": false, "authType": "SystemIdentity",
          "environmentVariables": [
            { "name": "ASPNETCORE_HTTP_PORTS", "value": "ASPNETCORE_HTTP_PORTS" },
            { "name": "ConnectionStrings__Sql", "value": "ConnectionStrings__Sql" },
            { "name": "Blob__ServiceUri", "value": "Blob__ServiceUri" },
            { "name": "Blob__Container", "value": "Blob__Container" },
            { "name": "Email__Mode", "value": "Email__Mode" },
            { "name": "Email__From", "value": "Email__From" },
            { "name": "Email__AzureCommunication__Endpoint", "value": "Email__AzureCommunication__Endpoint" },
            { "name": "Jwt__Key", "value": "Jwt__Key" },
            { "name": "Auth__GitHub__ClientId", "value": "Auth__GitHub__ClientId" },
            { "name": "Auth__GitHub__ClientSecret", "value": "Auth__GitHub__ClientSecret" },
            { "name": "Seed__AdminEmail", "value": "Seed__AdminEmail" },
            { "name": "Seed__AdminPassword", "value": "Seed__AdminPassword" },
            { "name": "PortsTree__Path", "value": "PortsTree__Path" },
            { "name": "PortsTree__GitUrl", "value": "PortsTree__GitUrl" },
            { "name": "PortsTree__Branch", "value": "PortsTree__Branch" },
            { "name": "RepoIndex__PublicBaseUrl", "value": "RepoIndex__PublicBaseUrl" },
            { "name": "APPLICATIONINSIGHTS_CONNECTION_STRING", "value": "APPLICATIONINSIGHTS_CONNECTION_STRING" } ] } }
    ]
    EOF
    az webapp sitecontainers create -g $RG -n $APP --sitecontainers-spec-file ./sitecontainers.json
    az webapp sitecontainers list -g $RG -n $APP
    az webapp sitecontainers status -g $RG -n $APP --container-name api
    az webapp sitecontainers log -g $RG -n $APP --container-name api
    az webapp restart -g $RG -n $APP     # the api may need a moment: nginx answers 502 until it listens on 8081
    az webapp log tail -g $RG -n $APP

    # deploy a new version: point both containers at the new tag (the commit SHA that CI pushed), then restart
    # az webapp sitecontainers update -g $RG -n $APP --container-name api --image $ACR.azurecr.io/renkuos-ports/api:<sha>
    # az webapp sitecontainers update -g $RG -n $APP --container-name web --image $ACR.azurecr.io/renkuos-ports/web:<sha>
    # az webapp restart -g $RG -n $APP

For a system-assigned identity the `Authentication=Active Directory Managed Identity` string needs no client id. A SQL-auth connection string stored in Key Vault is the fallback. The spec file uses `"authType": "SystemIdentity"` (the CLI flag equivalent is `--si` / `--system-assigned-identity true`), so both containers pull from ACR with the app's identity and need the `AcrPull` assignment above. After the first deploy, sign in as the seeded admin and run the recipe sync once to create the clone.

The spec file is created with a shell heredoc that expands `$ACR`; indent it as needed or write it with an editor. If you would rather not use a spec file, the portal route is Deployment Center > Containers (add the `web` container as main on port 8080 and the `api` container as a sidecar on port 8081, with environment variables referencing the app settings above).

### GitHub OAuth app

Create an OAuth app in GitHub (Settings, Developer settings). Homepage URL `https://<APP>.azurewebsites.net` (or the custom domain). Authorization callback URL `https://<public host>/signin-github`, i.e. `https://<APP>.azurewebsites.net/signin-github` or the custom domain, the default callback path of the GitHub handler (the app does not override it); nginx proxies that path to the api sidecar. The app requests the `user:email` scope. The redirect URI is built from the forwarded scheme and host (`UseForwardedHeaders` in `Program.cs`), which nginx sets to the public host. A user whose GitHub email matches an existing local account is refused rather than linked, by design.

## GitHub Actions outline

Use OIDC federation (no stored Azure secret): create an Entra app registration with a federated credential for the repository and branch, give it `AcrPush` on the registry and `Website Contributor` on the web app, and store `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` as repository variables.

    name: deploy
    on: { push: { branches: [main] }, workflow_dispatch: {} }
    permissions: { id-token: write, contents: read }
    jobs:
      test:
        runs-on: ubuntu-latest
        steps:
          - uses: actions/checkout@v4
          - uses: actions/setup-dotnet@v4
            with: { dotnet-version: 10.0.x }
          # tests need SQL Server and Azurite: start the compose services
          - run: cp .env.example .env && docker compose up -d sql azurite
          - run: dotnet test tests/RenkuPorts.Server.Tests
      deploy:
        needs: test
        runs-on: ubuntu-latest
        steps:
          - uses: actions/checkout@v4
          - uses: azure/login@v2
            with:
              client-id: ${{ vars.AZURE_CLIENT_ID }}
              tenant-id: ${{ vars.AZURE_TENANT_ID }}
              subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
          - run: az acr build -r <ACR> -t renkuos-ports/api:${{ github.sha }} .
          - run: az acr build -r <ACR> -t renkuos-ports/web:${{ github.sha }} -f nginx/Dockerfile .
          - run: |
              az webapp sitecontainers update -g <RG> -n <APP> --container-name api --image <ACR>.azurecr.io/renkuos-ports/api:${{ github.sha }}
              az webapp sitecontainers update -g <RG> -n <APP> --container-name web --image <ACR>.azurecr.io/renkuos-ports/web:${{ github.sha }}
              az webapp restart -g <RG> -n <APP>

This outline is an alternative to the repository's own `.github/workflows/ci.yml`, whose `push-images` job pushes both images to the registry, each tagged with the commit SHA and `latest`, with token secrets (it does not deploy). Deploying means pointing both sitecontainers at the new tag: either `sitecontainers update --image ...:<sha>` for each as above, or keep both on `:latest` and restart the app (App Service pulls the image again on restart; not verified for sitecontainers, so check the container status after a restart). Update both images together, since the nginx config and the API paths change together.

Notes: the compose file mounts `../RenkuOSPorts` for the `api` service only; `docker compose up -d sql azurite` does not start it, so the missing directory does not matter in CI. The SQL image is amd64, fine on hosted runners. Migrations run inside the app at startup by default, so no migration step is required; to move them into the pipeline set `Database__MigrateOnStartup=false` and add a step that generates `dotnet ef migrations script --idempotent -p src/RenkuPorts.Server -o migrate.sql` and applies it with an Entra identity before the deploy step. Consider a staging slot (Standard plan or higher) for swap-based deploys; note that migrations then run from whichever slot starts first.

## Rough monthly cost

Estimates in USD for small, always-on usage in a typical region; verify in the Azure pricing calculator, since prices vary by region and change.

| Resource | Tier | Approx. per month |
|---|---|---|
| App Service plan (Linux) | B1 about 13, B2 about 26; P0v3 about 55 to 70 for slots and more RAM. One app with two containers runs on the plan, so there is no second app to pay for, but both containers share the plan's memory (B1 has 1.75 GB, so prefer B2 or larger) | 13 to 70 |
| Azure SQL Database | Basic about 5, Standard S0 about 15 | 5 to 15 |
| Storage account (LRS, hot) | about 0.02 per GB stored plus operations; a few hundred GB of packages is single-digit dollars | 2 to 15, plus egress |
| Egress bandwidth | First 100 GB free, then roughly 0.08 per GB; downloads are proxied through the app (nginx, then the api sidecar) | 0 to 50 depending on traffic |
| Container Registry | Basic | about 5 |
| Key Vault | per 10k operations, negligible at this scale | under 1 |
| Application Insights / Log Analytics | pay per GB ingested after a free allowance | 0 to 10 |
| Communication Services Email | per email sent, failure notifications only | under 1 |
| Front Door Standard (optional later) | base fee plus per-request and per-GB charges | about 35 and up |

A small total, without Front Door, is on the order of 30 to 100 USD per month, dominated by the App Service plan and traffic.

## Pointing build clients at Azure

A build client needs outbound HTTPS only; it does not need to be on Azure. On this Mac, edit `~/Library/Application Support/renkuports-client/appsettings.json` and set `Server` to `https://<APP>.azurewebsites.net/` (or the custom domain), with the `ClientId` and `Secret` of a client created on the Azure instance's Clients page (client records are per database, so the local ones do not carry over). Then restart the agent: `launchctl kickstart -k gui/$(id -u)/org.renkuos.ports-client`. For a quick check use `dotnet run --project src/RenkuPorts.Client -- --Server https://<APP>.azurewebsites.net/ --ClientId <id> --Secret <secret> --BuildCommand <script> --once`. Before the first real build, test with `client/scripts/mock-build.sh` and then a package of realistic size to exercise the upload path described above.
