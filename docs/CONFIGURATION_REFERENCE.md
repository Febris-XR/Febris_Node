# Node configuration reference

**Generated 2026-08-23 from a read-side census of both node hosts, then corrected by hand for the
changes made the same day.** ROADMAP 18. This is THE one artefact for "what do I set": the
committed `appsettings.json` files are the defaults and the deploy template, `docker-compose.yml`
plus `.env` is the deploy substitution, and this file is the documentation. It replaces the
`.json.template` files (removed 2026-08-05) and the `_comment` keys that used to try to do this job
inside the JSON.

> **Scope.** The two node hosts, `Febris.UserNode.Portal` and `Febris.UserNode.Api`, plus the
> shared assemblies they reference. "Hosts" says which of the two reads a key. A key is LIVE when a
> reader exists AND the value reaches something that runs, traced to a file. The census that
> produced this was adversarially re-derived before anything was deleted on its evidence.

## How the three jobs are split

| Job | Where | What you do |
|---|---|---|
| **Committed default** | `enduser/<host>/appsettings.json` | Nothing. Values here are safe as shipped. Every `{Token}` in them is a deploy placeholder, NOT a default: the `ConfigurationPlaceholderValidator` refuses to boot a non-Development host with one unsubstituted. |
| **Deploy substitution** | `docker-compose.yml` -> `.env` (`selfhost/generate-env.sh` writes it) | Fill `.env`. Compose maps each variable onto a `Section__Key` environment variable, which overrides the JSON. |
| **Local override** | `enduser/<host>/appsettings.Development.json` (gitignored) | Copy the committed file, substitute what your box needs. Only loaded when `ASPNETCORE_ENVIRONMENT=Development`. |

Configuration sources, in precedence order (both `Program.cs`): `appsettings.json`, then
`appsettings.{Environment}.json`, then environment variables. `Section__Key` with a double
underscore is the environment-variable spelling of `Section:Key`.

## Environment variables with no JSON key

| Variable | Hosts | What it does | Default when unset |
|---|---|---|---|

| `ASPNETCORE_ENVIRONMENT` | both | Two different Development switches exist on the node: this env var (validator, and the central SSO at central/FebrisSSO/API/Startup.cs) versus the bu... | Validator runs in non-Development mode (warns/throws on placeholders). host environment u... |
| `FEBRIS_JWT_SIGNING_SECRET` | Api | Compose does NOT use this variable. it sets JwtSettings__Secret (docker-compose.yml), which is the config-path form. Either works. this one wins if b... | Falls through to JwtSettings:Secret. if that is also absent, the 'not configured' excepti... |
| `FEBRIS_JWT_SIGNING_PRIVATE_KEY` | Api | PEM text in an env var. Not set by compose. | Falls through to JwtSettings:PrivateKey, then to the HMAC-only (non-Dev) / ephemeral-RSA ... |
| `FEBRIS_JWT_SIGNING_KID` | Api | Only meaningful when an RSA key is present. | kid derived deterministically from the public key (DeriveKeyId :381-393) |

The JWT variables take precedence over `JwtSettings:Secret` / `:PrivateKey` / `:KeyId` in JSON.
`ForwardedHeaders__KnownNetworks__0` and `Identity__Registration__AutoProvisionJit` are set by
`docker-compose.yml` as ordinary `Section__Key` overrides of the sections below.

## The Development carve-out, stated plainly

In Development the JWT signing secret is NOT validated for strength. A fresh clone boots with the
literal string `{JwtTokenSecret}` as its HMAC key, and every device token the API mints is signed
with it. That is deliberate (it is why a clone starts at all) and it is now VISIBLE: the provider
records what it waived in `JwtSigningKeyProvider.DevelopmentSecretWaiver`, and the API logs it as a
warning at boot. (The Portal used to log the same waiver on the first node-admin token mint, until
ROADMAP 16 deleted the token and with it the Portal's only reason to construct the signing key at
all.) Outside Development the same secret fails the boot. The audit's earlier statement that the provider "fails closed on an
unsubstituted placeholder" is true only with that qualifier.

Also Development-only: an RSA signing pair is generated per run when none is configured, so the
JWKS path can be exercised locally. Tokens from a previous run stop validating after a restart.


## Must be substituted at deploy time


### `ConnectionStrings`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `ConnectionStrings:AnalyticsDBConnection` | both | LIVE | deploy-secret | Context not registered, no EnsureCreated, no health check. the analytics middleware's query cl... | `FebrisUserNodeDataAccessRegistration.cs` |
| `ConnectionStrings:DataDBConnection` | both | LIVE | deploy-secret | Context NOT registered (FebrisUserNodeDataAccessRegistration.cs), provisioner and health check... | `enduser/FebrisEndUserDAL/FebrisUserNodeDataAccessRegistration.cs` |
| `ConnectionStrings:MarketingDBConnection` | neither | INERT-NO-READER | residue | n/a | `AppConfiguration.cs` |
| `ConnectionStrings:TestUserXAPIDBConnection` | neither | INERT-NO-READER | residue | n/a | `AppConfiguration.cs` |
| `ConnectionStrings:UserDBConnection` | both | LIVE | deploy-secret | No literal default. Portal: provisioner skips (EndUserDatabaseProvisioner.cs), context registe... | `Startup.cs` |
| `ConnectionStrings:XAPIDBConnection` | both | LIVE | deploy-secret | Context not registered, provisioner/health skip, standard verbs + default Version never seeded... | `FebrisUserNodeDataAccessRegistration.cs` |

- **`ConnectionStrings:AnalyticsDBConnection`**: Placeholder {AnalyticsDBConnectionString} committed. compose docker-compose.yml for both hosts. Both hosts write this DB. only the Portal trims it.
- **`ConnectionStrings:DataDBConnection`**: Placeholder {DataDBConnectionString} committed in both templates. Compose docker-compose.yml for both hosts. The provisioner doc (EndUserDatabaseProvisioner.cs) claiming the Portal owns only UserDB is stale. the Portal registers and uses this database.
- **`ConnectionStrings:MarketingDBConnection`**: Not in either template.
- **`ConnectionStrings:TestUserXAPIDBConnection`**: Not in either template. Commented reads only.
- **`ConnectionStrings:UserDBConnection`**: Committed value is the placeholder {UserDBConnectionString} in both templates. contains the DB password so it is a secret. Compose: docker-compose.yml (shared anchor, both hosts). Unsubstituted placeholder is logged by ConfigurationPlaceholderValidator (API Startup.cs / Portal Startup.cs) in non-Development.
- **`ConnectionStrings:XAPIDBConnection`**: Placeholder {XAPIDBConnectionString} committed. compose docker-compose.yml for both hosts.

### `JwtSettings`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `JwtSettings:ExpiryTimeInSeconds` | both | LIVE | committed-default | 15 minutes (JwtLifetimeSettings.cs). Absent, blank, unparseable or <= 0 all fall back to 15 mi... | `shared/FebrisSharedServices/JwtLifetimeSettings.cs` |
| `JwtSettings:KeyId` | Api | LIVE | deploy-topology | Derived deterministically: first 16 chars of base64url(SHA-256(modulus \|\| exponent)) (Derive... | `JwtSigningKeyProvider.cs` |
| `JwtSettings:PrivateKey` | Api | LIVE | deploy-secret | Non-Development: no RSA key, HasAsymmetricKey=false, the API signs and validates HMAC only (J... | `JwtSigningKeyProvider.cs` |
| `JwtSettings:RefreshTokenHours` | Api | LIVE | committed-default | 8 hours (JwtLifetimeSettings.cs). blank/unparseable/<=0 fall back to 8 hours. | `JwtLifetimeSettings.cs` |
| `JwtSettings:Secret` | Api | LIVE | deploy-secret | Throws InvalidOperationException 'JWT signing secret is not configured' (JwtSigningKeyProvider... | `shared/FebrisSharedServices/JwtSigningKeyProvider.cs` |

- **`JwtSettings:ExpiryTimeInSeconds`**: Committed '900' in the API template (= the default). Portal template has no JwtSettings section so the Portal uses the 15-minute default unless the key is supplied. if an operator lengthens it on the API they must set it on the Portal too or the regeneration revocation window will be shorter than the live access token. Not set by compose.
- **`JwtSettings:KeyId`**: Optional. Not in any template, not set by compose. Only matters for key rotation with RS256.
- **`JwtSettings:PrivateKey`**: Optional. Not in any template, not set by compose. PEM may be PKCS#8 ('BEGIN PRIVATE KEY') or PKCS#1 ('BEGIN RSA PRIVATE KEY'). Since ROADMAP 16 only the API constructs the signing-key provider (the Portal's NodeAdmin token mint, the one Portal consumer, is deleted), so the same-key-on-both-hosts requirement that used to live here is gone with it.
- **`JwtSettings:RefreshTokenHours`**: Committed '8' in the API template (= the default). Not set by compose.
- **`JwtSettings:Secret`**: API template ships the placeholder {JwtTokenSecret} (appsettings.json) so an unsubstituted Release API fails at boot by design. Portal template has NO JwtSettings section at all. compose supplies JwtSettings__Secret=${NODE_JWT_SECRET} to both hosts through the anchor (docker-compose.yml), which is now one host more than reads it: since ROADMAP 16 the Portal neither mints nor validates JWTs, so only the API consumes the value. The compose line is harmless residue for the Portal container. Minimum 32 bytes (HMAC-SHA256).

### `FEBRIS_JWT_SIGNING_SECRET`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `FEBRIS_JWT_SIGNING_SECRET` | both | LIVE | env-only | Falls through to JwtSettings:Secret. if that is also absent, the 'not configured' exception (A... | `JwtSigningKeyProvider.cs` |

- **`FEBRIS_JWT_SIGNING_SECRET`**: Compose does NOT use this variable. it sets JwtSettings__Secret (docker-compose.yml), which is the config-path form. Either works. this one wins if both are present.

### `FEBRIS_JWT_SIGNING_PRIVATE_KEY`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `FEBRIS_JWT_SIGNING_PRIVATE_KEY` | both | LIVE | env-only | Falls through to JwtSettings:PrivateKey, then to the HMAC-only (non-Dev) / ephemeral-RSA (Dev)... | `JwtSigningKeyProvider.cs` |

- **`FEBRIS_JWT_SIGNING_PRIVATE_KEY`**: PEM text in an env var. Not set by compose.

### `FEBRIS_JWT_SIGNING_KID`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `FEBRIS_JWT_SIGNING_KID` | both | LIVE | env-only | kid derived deterministically from the public key (DeriveKeyId :381-393) | `JwtSigningKeyProvider.cs` |

- **`FEBRIS_JWT_SIGNING_KID`**: Only meaningful when an RSA key is present.

### `NodeBootstrap`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `NodeBootstrap:AdminEmail` | Portal | LIVE | deploy-topology | "admin@example.com" (NodeBootstrapAdminOptions.cs). Release build with default email AND no pa... | `Data/NodeBootstrapAdminOptions.cs` |
| `NodeBootstrap:AdminPassword` | Portal | LIVE | deploy-secret | null: Release creates a password-less account ONLY if AdminEmail was configured (Forgot Passwo... | `NodeBootstrapAdminOptions.cs` |

- **`NodeBootstrap:AdminEmail`**: NOT in any template. compose NodeBootstrap__AdminEmail from NODE_ADMIN_EMAIL (docker-compose.yml). Changing the email on a seeded node seeds a second admin (comment :192-196).
- **`NodeBootstrap:AdminPassword`**: NOT in any template. compose NodeBootstrap__AdminPassword from NODE_ADMIN_PASSWORD (docker-compose.yml). Only the email is ever logged. No other keys are read from this section.

### `EmailSender`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `EmailSender:* (API host copy)` | Api | INERT-NO-READER | residue | n/a, nothing reads it | `No reader: nothing under enduser/FebrisEndUserApi reference...` |
| `EmailSender:CheckCertificateRevocation` | Portal | LIVE | committed-default | false (preserves MailKit 2.x behaviour) | `EmailService.cs` |
| `EmailSender:EnableSSL` | Portal | LIVE | committed-default | false | `EmailService.cs` |
| `EmailSender:Host` | Portal | LIVE | deploy-topology | null Host. SendEmail fails and is logged (password reset / invite mail silently not delivered) | `shared/FebrisSharedServices/EmailService.cs` |
| `EmailSender:Password` | Portal | LIVE | deploy-secret | null | `EmailService.cs` |
| `EmailSender:Port` | Portal | LIVE | deploy-topology | 0 (GetValue<int>) | `EmailService.cs` |
| `EmailSender:Sender` | Portal | LIVE | deploy-topology | null | `EmailService.cs` |
| `EmailSender:SenderName` | Portal | LIVE | committed-default | null | `EmailService.cs` |

- **`EmailSender:* (API host copy)`**: ALREADY DELETED from enduser/FebrisEndUserApi/appsettings.json (2026-08-23; zero `EmailSender` occurrences in that template today, and this file records it at the `EmailSender (API host)` row of the "Removed on 2026-08-23" table below). The compose anchor still injects EmailSender__* into the API container (docker-compose.yml via *node-environment) where it is ignored.
- **`EmailSender:EnableSSL`**: Template false.
- **`EmailSender:Host`**: The API template no longer ships the section (removed 2026-08-23, see the "Removed on 2026-08-23" table below) and the API registers neither IEmailSender nor IUserLogic, so it is INERT there even when compose injects it. compose: EmailSender__Host from SMTP_HOST (docker-compose.yml).
- **`EmailSender:Password`**: Template {EmailSenderPassword}. compose SMTP_PASSWORD (docker-compose.yml).
- **`EmailSender:Port`**: compose default 587 (docker-compose.yml).
- **`EmailSender:Sender`**: Template {EmailSenderEmailAddress}. compose SMTP_SENDER (docker-compose.yml).
- **`EmailSender:SenderName`**: Template "Febris".

### `Storage`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `Storage:BasePath` | both | LIVE | deploy-topology | Falls back to SmbClient:Path. If both are blank: registration still succeeds (lazy factory, Fe... | `FebrisStorageRegistration.cs` |
| `Storage:Provider` | both | LIVE | deploy-topology | FileSystem (StorageOptions default, shared/FebrisSharedServices/Storage/IStorageProvider.cs. e... | `shared/FebrisSharedServices/Storage/FebrisStorageRegistration.cs` |
| `Storage:S3Bucket (also Storage:S3Endpoint, Storage:S3Region)` | both | LIVE | deploy-topology | S3Bucket blank -> InvalidOperationException 'Storage:S3Bucket is required for the S3 provider'... | `FebrisStorageRegistration.cs` |

- **`Storage:BasePath`**: Not in either template. compose sets /data/storage on a named volume for both hosts (docker-compose.yml). Ignored when Provider=S3.
- **`Storage:Provider`**: In NEITHER template. compose sets Storage__Provider=FileSystem (docker-compose.yml). Bound options are also registered as a StorageOptions singleton (:31).
- **`Storage:S3Bucket (also Storage:S3Endpoint, Storage:S3Region)`**: Not in any template or compose (compose is FileSystem). Phase 4 S3 path.

### `SmbClient`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `SmbClient:Path` | both | LIVE | deploy-topology | null. BaseFileSystemPath=null so every derived path becomes RELATIVE (e.g. 'media/video/...') ... | `shared/FebrisSharedServices/FileServerHandler.cs` |

- **`SmbClient:Path`**: Committed '{SmbClientPath}' placeholder in both templates. unsubstituted it creates literal '{SmbClientPath}...' directories under the CWD (present in the checkout: enduser/FebrisEndUserPortal/{SmbClientPath}*). Compose sets '/data/storage/' (docker-compose.yml). the TRAILING SLASH is load-bearing because every derived path is string concatenation. Should equal Storage:BasePath until the Phase 3 cutover retires the static path layer.

### `FileSystem`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `FileSystem:UniqueFileSystemPath` | both | LIVE | deploy-topology | null -> SpecificFileSystemPath == SmbClient:Path, i.e. the tenant subtree collapses onto the s... | `shared/FebrisSharedServices/FileServerHandler.cs` |

- **`FileSystem:UniqueFileSystemPath`**: Committed '{UniqueFileSystemPath}\\' in both templates. NOT flagged by ConfigurationPlaceholderValidator because IsUnsubstitutedTemplate requires the value to END with '}' (JwtSigningKeyProvider.cs IsUnsubstitutedTemplate, trailing backslash defeats it), so an unsubstituted Release deployment silently creates '{SmbClientPath}{UniqueFileSystemPath}' directories. Compose sets 'node/' (docker-compose.yml). trailing separator is load-bearing (concatenation).

### `AppKeys`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `AppKeys:KeyRingPath` | both | LIVE | deploy-topology | throws at boot: InvalidOperationException "AppKeys:KeyRingPath is not configured" (Startup.cs ... | `Startup.cs` |

- **`AppKeys:KeyRingPath`**: Both hosts MUST share one ring (SetApplicationName Febris.UserAuth) for Portal-minted tickets to be readable. compose mounts /data/keys into both (docker-compose.yml). Committed default "keys" is relative to the process CWD.

### `RedisConnectionStrings`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `RedisConnectionStrings:AuthConnection` | Portal | LIVE | deploy-topology | Absent, blank, or an unsubstituted {Token}: NO Redis session store, the ticket lives in the Da... | `Startup.cs` |
| `RedisConnectionStrings:HardwareConnection` | both | LIVE | deploy-topology | RedisCache is constructed with Configuration=null and throws on first use. HardwareRevocationL... | `Startup.cs` |

- **`RedisConnectionStrings:AuthConnection`**: The committed "localhost:6379" is NOT an absent value, so a bare `docker run` without Redis gets the Redis posture and login fails until the cookie store is reached. operators wanting the zero-dependency path must blank it. Unauthenticated host:port only. API JSON carries the key but the API registers no IDistributedUserCache (only health reads it at NodeHealthRegistration.cs and skips when not registered).
- **`RedisConnectionStrings:HardwareConnection`**: Compose docker-compose.yml points both at valkey:6379.

### `ForwardedHeaders`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `ForwardedHeaders:Enabled` | both | LIVE | env-only | true. with the whole section absent the middleware runs with XForwardedFor\|XForwardedProto an... | `shared/FebrisSharedServices/ForwardedHeadersConfiguration.cs` |
| `ForwardedHeaders:ForwardLimit` | both | LIVE | env-only | 1 hop (framework default when the section is absent. explicit default 1 when the section exist... | `ForwardedHeadersConfiguration.cs` |
| `ForwardedHeaders:KnownNetworks` | both | LIVE | deploy-topology | framework loopback-only trust (only a proxy on 127.0.0.1/::1 is honoured). the loopback defaul... | `ForwardedHeadersConfiguration.cs` |
| `ForwardedHeaders:KnownProxies` | both | LIVE | env-only | empty. framework loopback defaults retained unless KnownNetworks or KnownProxies has at least ... | `ForwardedHeadersConfiguration.cs` |
| `ForwardedHeaders:TrustAllProxies` | both | LIVE | env-only | false | `ForwardedHeadersConfiguration.cs` |

- **`ForwardedHeaders:Enabled`**: No JSON key in either template. nothing sets it in compose or .env. Only reason to set false is terminating TLS on the host itself and wanting no header trusted. Disabling behind a proxy breaks Request.Scheme (auth cookie SecurePolicy=Always when Redis is configured, HSTS) and collapses the rate limiter into one bucket.
- **`ForwardedHeaders:ForwardLimit`**: No JSON key, nothing sets it. A load balancer IN FRONT of an ingress is two hops. leaving this at 1 attributes traffic to the ingress address.
- **`ForwardedHeaders:KnownNetworks`**: NOT in either appsettings.json. Set ONLY by docker-compose.yml as ForwardedHeaders__KnownNetworks__0=172.28.0.0/16 through the *node-environment anchor, so both containers get it. the compose network is pinned to that subnet at docker-compose.yml. Array keys bind as KnownNetworks__0, __1... CIDR only. unparseable entries are logged and ignored (:126-130). Kubernetes operators must replace with their pod/ingress CIDR.
- **`ForwardedHeaders:KnownProxies`**: No JSON key, nothing sets it in compose/.env. Literal IP addresses for a fixed single proxy. prefer KnownNetworks where the proxy address is dynamic. Invalid entries are logged and ignored.
- **`ForwardedHeaders:TrustAllProxies`**: No JSON key, nothing sets it. Safe ONLY where the app is unreachable except via the ingress. The bundled compose stack deliberately does NOT use it because node-api also publishes port 8081 (docker-compose.yml, 162). a LAN caller there could forge X-Forwarded-For and bypass the rate limiter.

### `NodeIdentity`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `NodeIdentity:Name` | both | LIVE | deploy-topology | "Febris Node" (NodeIdentitySeeder.DefaultName :30). Only consulted when the NodeIdentity table... | `enduser/FebrisEndUserDAL/DataContext/NodeIdentitySeeder.cs` |

- **`NodeIdentity:Name`**: Not in either template, docker-compose.yml, .env.example or selfhost/generate-env.sh. Whichever host provisions DataDb first (the API, per compose depends_on ordering) is the one whose value sticks. Worth a line in the reference because it is the only operator-facing identity knob and it is silently ignored after first boot.

## Safe defaults you may leave alone


### `Identity`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `Identity:AccountLifecycle:AllowPersonalDataExport` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs). binding failure falls back to false (DownloadPersonalData.csh... | `Startup.cs` |
| `Identity:AccountLifecycle:AllowSelfServiceDeletion` | Portal | LIVE | committed-default | false (IdentityPolicyOptions.cs). binding failure falls back to false | `Startup.cs` |
| `Identity:AccountLifecycle:PurgeAfterDays` | Portal | LIVE | committed-default | null (IdentityPolicyOptions.cs) = never purge. null or <=0 purges nothing (SoftDeletedUserPurg... | `Startup.cs` |
| `Identity:AccountLifecycle:SoftDeleteOnly` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Lockout:EnabledForNewUsers` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Lockout:LockoutMinutes` | Portal | LIVE | committed-default | 15 (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Lockout:MaxFailedAttempts` | Portal | LIVE | committed-default | 5 (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Login:AllowLocalPassword` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs). binding failure also falls back to true (Login.cshtml.cs) | `Startup.cs` |
| `Identity:Login:AllowSelfServiceReset` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs). binding failure falls back to false (ForgotPassword.cshtml.cs) | `Startup.cs` |
| `Identity:Password:RequireDigit` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Password:RequireLowercase` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Password:RequireNonAlphanumeric` | Portal | LIVE | committed-default | false (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Password:RequireUppercase` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Password:RequiredLength` | Portal | LIVE | committed-default | 8 (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Password:RequiredUniqueChars` | Portal | LIVE | committed-default | 1 (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Registration:AllowedEmailDomains` | Portal | LIVE | committed-default | [] empty array (IdentityPolicyOptions.cs). under DomainAllowlist an empty list admits nobody (... | `Startup.cs` |
| `Identity:Registration:AllowedEmailDomains[]` | Portal | LIVE | committed-default | empty (only meaningful in DomainAllowlist mode) | `Startup.cs` |
| `Identity:Registration:AutoProvisionJit` | Portal | LIVE | committed-default | false (IdentityPolicyOptions.cs): the admin page's checkbox starts unchecked. | `Startup.cs` |
| `Identity:Registration:Mode` | Portal | LIVE | committed-default | AdminOnly (IdentityPolicyOptions.cs). a stored NodeRegistrationConfig row (Registration page) ... | `Startup.cs` |
| `Identity:Registration:RequireAdminApproval` | Portal | LIVE | committed-default | false (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Registration:RequireConfirmedEmail` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Session:AbsoluteTimeoutMinutes` | Portal | LIVE | committed-default | null (IdentityPolicyOptions.cs) = no absolute cap. null or <=0 disables (AbsoluteSessionTimeou... | `Startup.cs` |
| `Identity:Session:LifetimeMinutes` | Portal | LIVE | committed-default | 60 (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:Session:Sliding` | Portal | LIVE | committed-default | true (IdentityPolicyOptions.cs) | `Startup.cs` |
| `Identity:TwoFactor:Enforcement` | Portal | LIVE | committed-default | Off (IdentityPolicyOptions.cs). Off is a pure pass-through | `Startup.cs` |

- **`Identity:AccountLifecycle:PurgeAfterDays`**: Fails safe. Only meaningful if soft-deleted rows exist, which today requires AllowSelfServiceDeletion=true with SoftDeleteOnly=true. Pseudonymises the xAPI actor before deleting the Identity row (SoftDeletedUserPurger.cs).
- **`Identity:AccountLifecycle:SoftDeleteOnly`**: CONDITIONAL: the only consumer sits inside OnPostAsync, which is gated on AllowSelfServiceDeletion (:75). With the shipped AllowSelfServiceDeletion=false this value is never consulted. Admin-side UserLogic.Removal (UserLogic.cs) only removes cohort links and never deletes accounts, so there is no other deletion path. Read-side filters on IsDeleted (UserLogic.cs,1074,1225. UserQueries.cs) are unconditional and do not read this key.
- **`Identity:Lockout:EnabledForNewUsers`**: Setting false makes RequireAdminApproval's created-locked hold and DeletePersonalData's soft-delete lock depend on those paths setting LockoutEnabled explicitly (they do: UserLogic.cs, DeletePersonalData.cshtml.cs).
- **`Identity:Lockout:MaxFailedAttempts`**: Pairs with IpRateLimiting (5 per 15m on POST:/Identity/Account/Login) as a second, per-IP layer.
- **`Identity:Login:AllowLocalPassword`**: WARNING: false with no external provider compiled in (ExternalAuthProviderRegistration.cs all commented out) locks every user out of the portal. Only set false once an SSO provider is actually registered.
- **`Identity:Login:AllowSelfServiceReset`**: Reset also requires EmailSender to be configured. a blank SMTP host makes the flow silently fail at send time.
- **`Identity:Registration:AllowedEmailDomains`**: Only consulted when effective Mode=DomainAllowlist. Entries accept 'example.com' or '@example.com'. DB-first override: the stored row's comma-separated list supersedes (Resolver:209, SplitDomains:275-282).
- **`Identity:Registration:AllowedEmailDomains[]`**: The SSO path that also enforces it (ExternalLogin.cshtml.cs) is unreachable because no external provider is ever registered.
- **`Identity:Registration:AutoProvisionJit`**: as a gate it is unreachable (only the ExternalLogin page consults it, and no external scheme exists). As a VALUE it is live: the admin Registration page renders it as the checkbox default and it seeds the stored NodeRegistrationConfig row. Set it for that reason or not at all. The census's DEAD-CODE-PATH is right for the gate and wrong for 'the value goes nowhere': it is the admin page's seed default and the compose NODE_AUTO_PROVISION_JIT forwarding (docker-compose.yml) does pre-set what the operator sees.
- **`Identity:Registration:Mode`**: Values AdminOnly\|Invite\|Open\|DomainAllowlist (IdentityPolicy/IdentityPolicyEnums.cs). JSON value equals the code default, so the block may be omitted without change (appsettings.json says so). DeferredGates.Reasons is empty (DeferredGates.cs): all 23 Identity leaves are enforced.
- **`Identity:Registration:RequireAdminApproval`**: Reachable only when self-registration is enabled (Open/DomainAllowlist). Approval = admin lifting lockout (LockoutToggle). DB-first override applies (Resolver:210).
- **`Identity:Registration:RequireConfirmedEmail`**: CONFIG-ONLY: deliberately NOT part of the DB-first override (Resolver:213-216) because it is also copied into ASP.NET Identity at boot. Admin/bulk-provisioned and invited users are created pre-confirmed (UserLogic), so this gates only self-registered accounts. ForgotPassword.cshtml.cs also refuses unconfirmed accounts.
- **`Identity:Session:AbsoluteTimeoutMinutes`**: Stamped only at real sign-in. sessions already active when the cap is first enabled remain uncapped until re-auth (AbsoluteSessionTimeout.cs remarks). Chains with SecurityStampValidator rather than replacing it (Startup.cs).
- **`Identity:Session:LifetimeMinutes`**: Idle lifetime. Applies whether the ticket is in-cookie or in the Redis ticket store (NodeSessionPolicy.cs).
- **`Identity:Session:Sliding`**: With Sliding=true an active session renews indefinitely unless AbsoluteTimeoutMinutes is set.
- **`Identity:TwoFactor:Enforcement`**: Enum Off\|AdminsRequired\|AllRequired. AdminsRequired applies to Febris.Constants.RoleConstants.OrgAdmins (middleware:42). Enrollment pages (EnableAuthenticator etc.) exist under Areas/Identity/Pages/Account/Manage and are always allowed through (middleware:30-39). Enrolled verdict cached 5 min (middleware:46).

### `Transport`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `Transport:Cors:AllowCredentials` | both | LIVE | committed-default | true (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:Cors:AllowedHosts` | both | LIVE | deploy-topology | [] (NodeTransportOptions.cs) = only localhost and 127.0.0.1 origins allowed (NodeTransportOpti... | `Startup.cs` |
| `Transport:Cors:AllowedHosts[]` | both | LIVE | deploy-topology | empty: only localhost / 127.0.0.1 origins allowed | `Startup.cs` |
| `Transport:Hsts:Enabled` | both | LIVE | committed-default | true (shared/FebrisSharedServices/NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:Hsts:IncludeSubdomains` | both | LIVE | committed-default | true (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:Hsts:MaxAgeDays` | both | LIVE | committed-default | 365 (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:Hsts:Preload` | both | LIVE | committed-default | false (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:HttpsRedirection` | both | LIVE | committed-default | false (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:SecurityHeaders:XContentTypeOptions` | both | LIVE | committed-default | true (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:SecurityHeaders:XFrameOptions` | both | LIVE | committed-default | "SameOrigin" (NodeTransportOptions.cs) | `Startup.cs` |
| `Transport:SecurityHeaders:XXssProtection` | both | LIVE | committed-default | true (NodeTransportOptions.cs) | `Startup.cs` |

- **`Transport:Cors:AllowCredentials`**: Valid with credentials because the origin predicate never yields '*'.
- **`Transport:Cors:AllowedHosts`**: Hostnames, not URLs: 'app.example.com' exact, '.example.com' = domain + subdomains. Shipped empty in both templates. the bundled compose stack is same-origin through Caddy so it stays empty there. Must be set by any operator serving a frontend from another host.
- **`Transport:Cors:AllowedHosts[]`**: Exact host or leading-dot domain. Template [] on both hosts.
- **`Transport:Hsts:Enabled`**: BOTH hosts since ROADMAP 5. Each calls AddHsts from this section and gates app.UseHsts() on this flag, in non-Development only. Turn it off when TLS terminates at a proxy that owns the header itself. HSTS is only emitted when Request.IsHttps, which behind a proxy depends on ForwardedHeaders (XForwardedProto). Until 2026-08-24 the API ignored this section and ran app.UseHsts() on framework defaults (30 days, no includeSubDomains, no preload), so an operator hardening HSTS configured one of their two hosts.
- **`Transport:Hsts:IncludeSubdomains`**: Both hosts. Defaults true, which is STRONGER than the framework default the API used before ROADMAP 5.
- **`Transport:Hsts:MaxAgeDays`**: Both hosts. Defaults 365, against the framework's 30 that the API used before ROADMAP 5.
- **`Transport:Hsts:Preload`**: Both hosts. Only set true after submitting to the preload list.
- **`Transport:HttpsRedirection`**: Both hosts since ROADMAP 5. Off by default because the bundled Caddy terminates TLS and an app-level redirect would loop. The API expressed that default as a COMMENTED-OUT UseHttpsRedirection line, which is not the same thing: the key could not be turned on there at all.
- **`Transport:SecurityHeaders:XContentTypeOptions`**: Both hosts. The API used to hardcode nosniff unconditionally. It now reads this flag, so the two hosts answer an operator identically.
- **`Transport:SecurityHeaders:XFrameOptions`**: Both hosts since ROADMAP 5 (the API emitted no X-Frame-Options at all before). Values: 'Off' omits the header, 'Deny' blocks all framing, 'SameOrigin' or ANY unrecognised value (typo) fails safe to SameOrigin, on both hosts.
- **`Transport:SecurityHeaders:XXssProtection`**: Both hosts since ROADMAP 5 (the API emitted no X-XSS-Protection before).

### `IpRateLimiting`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `IpRateLimiting:ClientIdHeader` | both | LIVE | committed-default | "X-ClientId" (library default, same as template) | `Startup.cs` |
| `IpRateLimiting:EnableEndpointRateLimiting` | both | LIVE | committed-default | false (library default): rules match by period only, the per-endpoint POST rules collapse into... | `Startup.cs` |
| `IpRateLimiting:HttpStatusCode` | both | LIVE | committed-default | 429 (library default) | `Startup.cs` |
| `IpRateLimiting:RealIpHeader` | both | LIVE | committed-default | "X-Real-IP" (library default, string present in AspNetCoreRateLimit 5.0.0 assembly): the heade... | `Startup.cs` |
| `IpRateLimiting:StackBlockedRequests` | both | LIVE | committed-default | false | `Startup.cs` |

- **`IpRateLimiting:ClientIdHeader`**: Only matters for ClientWhitelist, which neither template sets.
- **`IpRateLimiting:EnableEndpointRateLimiting`**: Template true on both hosts.
- **`IpRateLimiting:RealIpHeader`**: Template deliberately "" so resolution falls to Connection.RemoteIpAddress as corrected by ForwardedHeaders. OMITTING the key is NOT equivalent to the shipped value. Keep it present and empty.

### `AnalyticsRetention`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `AnalyticsRetention:AnonymiseAfterDays` | Portal | LIVE | committed-default | 90 (AnalyticsRetentionReaper.cs,99). <=0 disables (:144-148) | `AnalyticsRetentionReaper.cs` |
| `AnalyticsRetention:PurgeAfterDays` | Portal | LIVE | committed-default | 365 (AnalyticsRetentionReaper.cs,98). 0 or negative DISABLES the purge (:108-112) | `enduser/FebrisEndUserBLL/Logic/AnalyticsLogic/AnalyticsRetentionReaper.cs` |

- **`AnalyticsRetention:AnonymiseAfterDays`**: Runs before the purge each pass (AnalyticsRetentionService.cs). Portal only, same reason as PurgeAfterDays.
- **`AnalyticsRetention:PurgeAfterDays`**: Defaults ON, unlike the two account/video purgers. Deliberately not registered on the API host (API Startup.cs registers only VideoRetention at :159-162). an API-only deployment never trims analytics (recorded in docs/BUGS.md per AnalyticsRetentionService.cs). Per-run ceiling 50000 rows (:76).

### `VideoRetention`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `VideoRetention:AbandonedPartDays` | Api | LIVE | committed-default | 7. <=0 disables | `enduser/FebrisEndUserBLL/Logic/DataLogic/VideoRetentionReaper.cs` |
| `VideoRetention:PurgeAfterDays` | Api | LIVE | committed-default | null: finished-recording purge disabled | `VideoRetentionReaper.cs` |

- **`VideoRetention:AbandonedPartDays`**: NOT in any template. safe to omit.
- **`VideoRetention:PurgeAfterDays`**: NOT in any template.

### `VideoLimits`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `VideoLimits:MaxPartBytes` | Api | LIVE | committed-default | 16 MiB (DefaultMaxPartBytes) | `enduser/FebrisEndUserBLL/Logic/LauncherLogic/VideoUploadLogic.cs` |
| `VideoLimits:MaxPartsPerRecording` | Api | LIVE | committed-default | 640 (DefaultMaxPartsPerRecording) | `VideoUploadLogic.cs` |

- **`VideoLimits:MaxPartBytes`**: FIXED 2026-08-23: the API now registers IVideoFileHandler, so the greedy constructor that reads this key is the one MS.DI resolves. Before the fix the legacy constructor ran and this key was ignored. NOT in any template. The code comment at :78-80 names this exact degradation.
- **`VideoLimits:MaxPartsPerRecording`**: see MaxPartBytes. NOT in any template.

### `UploadLimits`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `UploadLimits:MaxMultipartBodyBytes` | both | LIVE | committed-default | 10737418240 (10 GiB) on both hosts | `Startup.cs` |

- **`UploadLimits:MaxMultipartBodyBytes`**: NOT in any template. safe to omit. API comment (:202-212) explains why it is not lowered (package ingest).

### `HealthChecks`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `HealthChecks:DetailedResponse` | both | LIVE | committed-default | Detailed on Development, terse everywhere else (`configured ?? isDevelopment`) | `enduser/FebrisEndUserBLL/Logic/HealthLogic/NodeHealthRegistration.cs` |

- **`HealthChecks:DetailedResponse`**: ADDED TO THIS FILE 2026-08-27. It was missing entirely, which
  is the defect: `grep -c HealthChecks docs/CONFIGURATION_REFERENCE.md` returned **0** while the key
  is real, operator-facing, and read by both hosts. Declared as
  `public const string DetailedResponseKey = "HealthChecks:DetailedResponse";`
  (`NodeHealthRegistration.cs:145`) and resolved at `:160` as a **nullable** read, so leaving it unset
  is not the same as setting it `false`. Both node hosts consume it:
  `enduser/FebrisEndUserApi/Startup.cs:604` and `enduser/FebrisEndUserPortal/Startup.cs:943`, each
  having registered `AddNodeHealthChecks` at `:385` and `:639` respectively.
  [`SELF_HOSTING.md`](SELF_HOSTING.md) instructs operators to set it to `true` to get per-check detail
  back on a private network, and points at this file as the complete per-key reference, so its absence
  here sent operators to a document that did not answer them. **NOT in any template**, which is the
  same shape as `VideoLimits` and `UploadLimits` above and is why the Category is `committed-default`:
  the default lives in code rather than in `appsettings.json`.
- **On the source guard**: `ConfigurationSurfaceGuardTests` does **not** need extending for this key.
  `Every_template_section_has_a_reader_in_the_node_graph` iterates the sections in each template and
  checks each has a reader. It runs template to reader, not reader to template, so a key with a reader
  and no template section cannot trip it. Verified by reading the test, not assumed.

### `Branding`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `Branding:LogoUrl` | Portal | LIVE | deploy-topology | "" (no logo in mail) | `EmailService.cs` |
| `Branding:SchedulingUrl` | Portal | LIVE | deploy-topology | "" | `EmailService.cs` |
| `Branding:UnsubscribeBaseUrl` | Portal | LIVE | deploy-topology | "" (link rendered as bare recipient UUID) | `shared/FebrisSharedServices/EmailService.cs` |

- **`Branding:LogoUrl`**: NOT in any template.
- **`Branding:SchedulingUrl`**: NOT in any template.
- **`Branding:UnsubscribeBaseUrl`**: NOT in any template.

### `HubFederation`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `HubFederation:AuthenticationApi` | both | LIVE | deploy-topology | null. CanReachAuthenticationApi false | `HubFederationModels.cs` |
| `HubFederation:DataApi` | both | LIVE | deploy-topology | null. CanReachDataApi false | `HubFederationModels.cs` |
| `HubFederation:Enabled` | both | LIVE | deploy-topology | section absent: legacy keys govern. section present but key absent: false. Demoted to false wh... | `HubFederationModels.cs` |
| `HubFederation:LicenseKey` | both | LIVE | deploy-secret | null. HasLicenseKey false (scheme-B license bootstrap skipped) | `HubFederationModels.cs` |

- **`HubFederation:AuthenticationApi`**: NOT in any template.
- **`HubFederation:DataApi`**: NOT in any template.
- **`HubFederation:Enabled`**: NOT in any template. Consumers: ~27 remote query classes, TokenQueries, HubFederationHealthCheck (always registered, NodeHealthRegistration.cs), HubSyncLogic, NodeStatusLogic. The portal Hub Federation page writes the DB row, which then beats this key within 15 s (CacheTtl).
- **`HubFederation:LicenseKey`**: NOT in any template. The DB-row equivalent is stored encrypted via the DAL's DataProtection protector, so it depends on AppKeys:KeyRingPath persistence.

### `ConfigValidation`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `ConfigValidation:FailFastOnUnresolvedPlaceholders` | both | LIVE | committed-default | false: unresolved {Placeholder} values are only logged as a warning | `shared/FebrisSharedServices/ConfigurationPlaceholderValidator.cs` |

- **`ConfigValidation:FailFastOnUnresolvedPlaceholders`**: NOT in any template. GOTCHA: the single-arg Validate() decides Development from the ASPNETCORE_ENVIRONMENT env var (:62-67), not from IWebHostEnvironment. Program.cs forces the host environment via UseEnvironment without setting that var, so a DEBUG run without ASPNETCORE_ENVIRONMENT still scans (warn-only) and a Release run with ASPNETCORE_ENVIRONMENT=Development skips the scan entirely.

### `Serilog`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `Serilog:FailFastOnSinkBindingErrors` | both | LIVE | local-override | false: binding problems are printed to stderr (:82) and the host continues, possibly logging n... | `shared/FebrisSharedServices/SerilogStartupValidator.cs` |
| `Serilog:WriteTo:1:Name=RollingFile + Args:pathFormat (Dev overlays)` | neither | DEAD-CODE-PATH | local-override | Omitting the overlay entry restores the committed File sink at index 1. | `Present only in API appsettings.Development.json and Portal...` |
| `Serilog:WriteTo:1:Name=RollingFile + Serilog:WriteTo:1:Args:pathFormat (appsettings.Development.json only)` | neither | DEAD-CODE-PATH | local-override | Omitting the overlay entry restores the committed File sink. The pathFormat value goes nowhere... | `Program.cs` |

- **`Serilog:FailFastOnSinkBindingErrors`**: Optional opt-in. Not in any template or compose. Sibling of ConfigValidation:FailFastOnUnresolvedPlaceholders.
- **`Serilog:WriteTo:1:Name=RollingFile + Args:pathFormat (Dev overlays)`**: DEBUG runs log to Console only and print the LOG-B1 warning (SerilogStartupValidator.cs). Delete or convert to Name=File/Args.path.
- **`Serilog:WriteTo:1:Name=RollingFile + Serilog:WriteTo:1:Args:pathFormat (appsettings.Development.json only)`**: Overlay-only key with a Windows path (C:\\Febris\\Portal\\Logs\\log-{Date}.json, same path in BOTH hosts' overlays, so even if a RollingFile package were added both dev hosts would write one file). The '{Date}' token is deliberately not treated as a deploy placeholder (ConfigurationPlaceholderValidator.cs). Recommend deleting from both overlays or converting to Name=File/Args.path.

### `AllowedHosts`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `AllowedHosts` | both | LIVE | committed-default | '*' (framework fallback when the key is missing or empty) = no host filtering. | `Program.cs` |

- **`AllowedHosts`**: Committed '*' in both templates (API appsettings.json, Portal :163) and both dev overlays. not set by compose (Caddy fronts the hosts). Do NOT confuse with Transport:Cors:AllowedHosts (shared/FebrisSharedServices/NodeTransportOptions.cs), which is the CORS origin allowlist read at API Startup.cs:~398/~518 and Portal Startup.cs:~297/~901.

### `ASPNETCORE_ENVIRONMENT`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | both | LIVE | env-only | Validator runs in non-Development mode (warns/throws on placeholders). host environment unaffe... | `shared/FebrisSharedServices/ConfigurationPlaceholderValidator.cs` |

- **`ASPNETCORE_ENVIRONMENT`**: Two different Development switches exist on the node: this env var (validator, and the central SSO at central/FebrisSSO/API/Startup.cs) versus the build configuration (IsDevelopment(), the JwtSigningKeyProvider carve-out, Development overlay loading). Docker images set neither.

## Read, but with a caveat


### `PackageFeed`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `PackageFeed:Url` | portal | LIVE | operator-choice | empty, so no automatic sync runs at all | `enduser/FebrisEndUserPortal/BackgroundTasks/PackageFeedSyncService.cs` |
| `PackageFeed:Channel` | portal | LIVE | operator-choice | `stable` | same |
| `PackageFeed:IntervalHours` | portal | LIVE | operator-choice | 24. values below 1 are raised to 1 | same |

- **`PackageFeed:Url`**: HTTPS manifest the node syncs its software catalogue from, on a schedule. **Empty by default**, which leaves the service idle: it logs that it is idle at boot and reaches out to nothing. That is the correct posture for an air-gapped node and for any operator who prefers to run syncs by hand from the portal, which still works unchanged.

  This carries more weight than a convenience setting. The manual package-upload path was removed on 2026-08-31, so a feed is now the **only** way packages reach a node's catalogue, and a node holding nothing cannot serve devices. The Mobile Server fetches the Companion **from the node** over the device API, so headsets cannot be updated until the catalogue holds it.

  The sync itself is unchanged and still enforces its own gates: absolute https only, a 4 MiB manifest ceiling, a 512 MiB artifact ceiling, sha256 verified before anything is ingested, and a held package whose checksum changed is refused rather than overwritten.

- **`PackageFeed:Channel`**: entries on other channels are filtered out and reported as such.

- **`PackageFeed:IntervalHours`**: a feed changes only when a release ships, so this is a safety net rather than a poll. Anything below one hour is raised to one hour and a warning is logged. The first run is delayed two minutes after boot so it does not race startup migrations and seeding.

  A scheduled run is never a dry run. A scheduled dry run would report and change nothing forever, which is a silent no-op. The portal form keeps its dry-run option for an operator who wants to look before committing.

### `ClientDownloads`

| Key | Hosts | Status | Category | Default when absent | Read at |
|---|---|---|---|---|---|
| `ClientDownloads:BaseUrl` | portal | LIVE | operator-choice | `https://www.febr.is`. an absent section keeps that default, so link-out works on a node nobody configured | `shared/FebrisSharedServices/ClientDownloadOptions.cs` |

- **`ClientDownloads:BaseUrl`**: Where the Software Repository pages send an operator when this node holds **no local copy** of a client package. A node's catalogue starts empty and only fills through a manual portal upload or a feed sync, and nothing obliges a self-host operator to do either, so before this those pages were a permanent dead end on every fresh deployment for software that does exist.

  **Local package always wins.** This is consulted only when nothing is held, so an operator who uploads their own build keeps serving it and never sees an external link. **Blank it to disable link-out entirely**: an air-gapped node then shows the plain empty state and renders no external URL at all, which is the supported air-gap posture. A LAN mirror is the other option, since any `http(s)` root serving the same page works, including one under a subdirectory.

  A value that is not an absolute `http` or `https` URL is rejected and treated as disabled, deliberately, so a typo cannot become a relative link resolving against the node's own host and sending an operator to a 404 on their own portal.

  **Rendering a link is not a network call.** The node never requests these URLs and sends nothing anywhere. Only the operator's browser travels, and only if they click, so the node's offline-first posture is unchanged. The per-kind anchors this appends (`#pc`, `#mobile-server`, `#mobile-companion`, `#sdk-csharp`, `#sdk-cpp`) are a contract with the landing site generator and are pinned by `ClientDownloadOptionsTests`.

### `GeoDataUrls` (REMOVED)

**No `GeoDataUrls` key exists on either host, and nothing reads one.** The section is kept here so
an operator upgrading from an earlier node, or reading an older template, can see that its absence
is deliberate rather than an omission.

It configured a geocoding endpoint called from `LocationLogic.Create` and `.Update` to stamp
coordinates onto a saved Location. Three independent facts made it residue rather than a feature.

1. **Nothing read the coordinates.** Their only consumer was the Leaflet map partial on
   `Views/Location/Index.cshtml`, which the owner ruled out and ROADMAP 18 removed.
2. **The callers were unreachable anyway.** Both sit behind
   `if (!IsLocalAdmin() || !IsLocalFebrisAdmin()) return null`, a De Morgan inversion demanding
   BOTH Admin and SuperAdmin, and SuperAdmin is not a node role. The guard bug is recorded in
   `docs/BUGS.md` and is unaffected by this removal.
3. **The key shipped blank, so the call could not succeed.** An empty value threw
   `UriFormatException` inside `Geocoder.GetGeoCodes`, whose catch suppressed it and returned the
   fallback coordinates 39.8283 and -98.5795, the geographic centre of the United States.

Removing the callers therefore changes no observable behaviour. What it does change is the node's
network posture, which is why it is worth recording. `Geocoder` was the last thing that could
originate an outbound request without an operator configuring one. A node now reaches the network
only for destinations an operator has explicitly named, namely mail and the client artifact feed.

`Geocoder` itself still exists in the published `Febris.SharedServices` package and is untouched
here, because removing a public type from a released package is a breaking change for no gain. It
has no caller in this repository.


## Not wired: external identity providers

The Portal template used to carry an `ExternalAuthProviders` section with Google, Microsoft and
OpenID Connect entries. It was scaffolding: every `AddGoogle`, `AddMicrosoftAccount` and
`AddOpenIdConnect` call in `LocalUtility/ExternalAuthProviderRegistration.cs` is commented out and
no authentication package is referenced by the Portal project, so `Enabled: true` had exactly the
effect of `false`. The section is gone from the template so that nobody configures a provider and
waits for it to work. The registration code and its options type remain, as the starting point.

To wire a provider for real: add the NuGet package (`Microsoft.AspNetCore.Authentication.Google`,
`...MicrosoftAccount`, or `...OpenIdConnect`), uncomment the matching `AddXxx` call, and then
configure, in this shape:

```json
"ExternalAuthProviders": {
  "Google":    { "Enabled": true, "ClientId": "...", "ClientSecret": "..." },
  "Microsoft": { "Enabled": true, "ClientId": "...", "ClientSecret": "..." },
  "OpenIdConnect": [
    { "Enabled": true, "Scheme": "azure-ad", "DisplayName": "Azure AD",
      "Authority": "https://login.microsoftonline.com/{tenant-id}/v2.0",
      "ClientId": "...", "ClientSecret": "..." },
    { "Enabled": true, "Scheme": "okta", "DisplayName": "Okta",
      "Authority": "https://{your-okta-domain}/oauth2/default",
      "ClientId": "...", "ClientSecret": "..." }
  ]
}
```

`Identity:Registration:AutoProvisionJit` only gates anything once an external scheme exists.

## Removed on 2026-08-23, and why

Each of these was in one or both templates and is read by nothing that runs on a node. A guard in
`tests/FebrisArchitectureTests/ConfigurationSurfaceGuardTests.cs` fails the build if any returns.

| Key | Why it went |
|---|---|

| `NodeAdminToken:LifetimeMinutes` | removed 2026-08-23 with ROADMAP 16: the NodeAdmin token it tuned is deleted (the admin-only API writes moved to the Portal behind cookie auth), so its reader NodeAdminAuthorization.cs no longer exists. |
| `CertificationSettings` | bound on the API, injected by nothing in any tier, never bound on the Portal. Binding and type deleted. |
| `KeyPersistence` | read only by the Developer Portal. Node hosts persist DataProtection keys from AppKeys:KeyRingPath. |
| `AppKeys:RedisCache` | read by nothing repo-wide. |
| `UsingRevProxy` | no reader in the node graph. |
| `SmbClient:Secret` | read into a NetworkCredential inside a CredentialCache that is never handed to any I/O call. The real I/O is System.IO. |
| `SmbClient:UserName` | same as SmbClient:Secret. |
| `LicenseKey` | legacy hub-federation fallback pair with ApiUrlPath. The code path stays for existing deployments (HubFederationGateTests), the templates stop advertising it. Configure HubFederation instead. |
| `ApiUrlPath` | see LicenseKey. |
| `EmailSender (API host)` | no IEmailSender registration and no mail consumer on the API host. The Portal keeps its section. |
| `GeoDataUrls` (both hosts) | the API host never referenced Geocoder. The Portal's `GeoCoderServerAPIUrl` has now gone too, since its callers were removed. See the `GeoDataUrls` (REMOVED) section above. |
| `GeoDataUrls:TileServerAPIUrl` | fed a Leaflet map broken at three levels. The whole map surface was removed per the owner ruling "remove the map surface, do not vendor the library". |
| `JwtSettings:Issuer` | the API's AddJwtBearer registration never executes (UseAuthentication is commented out, and the custom filter validates with ValidateIssuer=false) and the mint stamps no iss claim. |
| `JwtSettings:Audience` | same as Issuer, ValidateAudience=false and no aud claim minted. |
| `JwtSettings:Subject` | no reader at all. |
| `ExternalAuthProviders` | every AddGoogle / AddMicrosoftAccount / AddOpenIdConnect call in the registration is commented out and no auth package is referenced, so Enabled=true does nothing. The worked examples are preserved below under "Not wired". |
| `IpRateLimitPolicies` | never in the templates, and no Configure<IpRateLimitPolicies> on either host. Listed so nobody adds it expecting per-IP policies. |
| `RedisConnectionStrings:LicenseConnection` | commented-out placeholder line, and its only factory is commented out too. |

## Local development: the overlay, and two things in it that do nothing

`appsettings.Development.json` on a dev box typically sets the four connection strings, the two
Redis strings, `AppKeys:KeyRingPath`, `SmbClient:Path` / `FileSystem:UniqueFileSystemPath`, and on
the Portal the `EmailSender` block. Two patterns seen in real overlays do nothing:

- A Serilog `WriteTo` entry with `"Name": "RollingFile"` and a `pathFormat`. `Serilog.Sinks.RollingFile`
  is referenced by no project, so the entry is ignored and the committed `File` sink is what writes.
- `JwtSettings` keys other than `Secret`, `ExpiryTimeInSeconds` and `RefreshTokenHours`. See the
  removed-keys table.

Redis on a dev LAN is typically unauthenticated (`host:port` only). Set `requirepass` and add the
password to both `RedisConnectionStrings` before the box is reachable from outside the LAN.

## Keeping this file true

The census behind it is reproducible: the source guard above pins that every template section has
a literal reader in the node graph, the removed keys are pinned by name, and
`tests/mutation/run-mutations.py config-surface` proves both guards can fail. When a key is added,
changed or removed, update this file in the same change.
