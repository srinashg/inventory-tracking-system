# Deployment Security Checklist

What is already provided by the Docker Compose stack in this repository (a **local** stack), what must still be done before TechStore serves real users on a network, and what is known to be missing. How to run the stack is in `README.md`; how it is built is in `architecture.md` §8.

Legend: **✅ provided and tested** · **☐ you must do this for a real deployment** · **⏳ deferred to a later milestone**

---

## 1. Already provided (and what verifies it)

| ✅ | How | Verified by |
|---|---|---|
| Secrets are not in the repository, images or logs | `.env` (git-ignored) → container environment variables; `.env.example` has placeholders only | `compose-stack.sh`, `compose-e2e.sh` scan every image's environment and history and every log for the generated secrets |
| First Admin from configuration, removable afterwards | `TECHSTORE_ADMIN_EMAIL/PASSWORD` used only when no Admin exists; blank them and recreate the API container; a warning (no values) logs while they remain | `compose-stack.sh`; `AuthInitializerTests` |
| Brand-new database without Admin settings refuses to start | The API exits with a message naming `Auth:InitialAdmin` and no value | `compose-stack.sh` |
| Test accounts cannot exist in a real environment | The API refuses `Auth:TestAccounts` unless the environment is `Testing` | `AuthInitializerTests`; Compose runs `Production` |
| Least-privilege database user | The API connects as `techstore_app` (no superuser, no create/drop/alter, no `COPY ... PROGRAM`, no write to the migration history; `DELETE` only on `user_roles`); migrations run as the owner in the one-shot `migrate` container | `LeastPrivilegeDatabaseTests`, `compose-stack.sh`, `compose-e2e.sh` |
| Future migrations keep working for the API | Default privileges in `backend/database/Provisioning/app-role.sql` | `LeastPrivilegeDatabaseTests` |
| Migrations are idempotent and non-destructive | One EF Core idempotent SQL script; the stack test fails if a migration ever adds `DROP`, `TRUNCATE` or `DELETE FROM` | `compose-stack.sh`; restarts and manual reruns change nothing |
| Passwords stored only as ASP.NET Core Identity hashes; weak passwords refused; lockout; login rate limit | Identity password hasher, `WeakPasswordValidator`, 5 failures → 15 min, 10 attempts/min/client, 100/min overall | `AuthInitializerTests`, `AccountServiceTests`, `HardeningApiTests`, Playwright |
| Cookies: HttpOnly, SameSite=Strict, `Secure` unless explicitly relaxed; CSRF on every change | `AuthServiceCollectionExtensions`; the API refuses `Auth:RequireSecureCookie=false` outside Development/Testing unless `Auth:AllowInsecureCookiesForLocalHttp=true` is also set, and warns at startup when it is | `DeploymentConfigApiTests`, `AuthInitializerTests`, Playwright |
| Client address cannot be forged for the rate limit | `Auth:TrustedProxies` lists exactly one address (the web container's fixed address); nginx **overwrites** `X-Forwarded-For`; the API believes only the last entry from that address | `DeploymentConfigApiTests`, `web-image.sh`, `compose-stack.sh` |
| Sessions survive restarts | Data Protection key ring on the `keys` volume (`DataProtection__KeysPath=/keys`) | `compose-stack.sh`, `compose-e2e.sh` |
| Data survives restarts and `down` | Named volume `pgdata` | `compose-stack.sh`, `compose-e2e.sh` |
| Only the web container is reachable, only from this computer | `127.0.0.1:8080`; `db` and `api` publish nothing | `compose-stack.sh` |
| Containers run as non-root, with no extra privileges | API uid 1654, nginx uid 101; `no-new-privileges`, all capabilities dropped | `compose-stack.sh` |
| Static-site security headers | Content-Security-Policy (own scripts and styles only, no framing), nosniff, Referrer-Policy, X-Frame-Options, Permissions-Policy; the API sets its own on `/api` including `Cache-Control: no-store` | `web-image.sh`, `HardeningApiTests` |
| Backup and restore procedure | `README.md` → "Backup and restore" | `backup-restore.sh` (in place and into a brand-new stack, damaged file changes nothing) |

---

## 2. Required before a real deployment

### Network and HTTPS
- ☐ Terminate **HTTPS** in front of the web container (a TLS-terminating proxy or load balancer) and serve the React app and the API from the **same site**; the cookie is `SameSite=Strict`.
- ☐ Remove `Auth__RequireSecureCookie: "false"` and `Auth__AllowInsecureCookiesForLocalHttp: "true"` from the deployment configuration. **Do not copy those two lines from `compose.yaml`.** Confirm the session cookie then carries `Secure`.
- ☐ Add HSTS at the TLS layer once HTTPS works everywhere. Consider the `__Host-` cookie name prefix.
- ☐ Do not publish the web port on a public interface over plain http (`compose.yaml` binds `127.0.0.1` for this reason). Publish only the HTTPS endpoint.
- ☐ If the TLS proxy sits in front of nginx, the address nginx sees is that proxy's. Decide who is the "trusted proxy" for `X-Forwarded-For` (the TLS proxy, or nginx behind it), list its single address in `Auth:TrustedProxies`, and make that proxy **overwrite** the header. Otherwise the login rate limit sees one client for everybody.
- ☐ Set `AllowedHosts` to the real host names instead of `*`.
- ☐ Rebuild the CSP if the site needs to load anything from another origin (it currently allows none).

### Secrets
- ☐ Move secrets out of a plain `.env` file and out of `docker inspect` output: a secret store or file-based secrets (PostgreSQL `*_FILE` variables, a key-per-file configuration source for ASP.NET Core). Anyone who can run Docker commands on the host can read environment variables today.
- ☐ Use three different strong secrets: `POSTGRES_PASSWORD`, `TECHSTORE_APP_DB_PASSWORD`, and the Admin password. Rotate them on a schedule: `TECHSTORE_APP_DB_PASSWORD` is re-applied by `migrate` on every start; changing `POSTGRES_PASSWORD` needs `ALTER ROLE` inside the database.
- ☐ After the first start, remove `TECHSTORE_ADMIN_EMAIL` and `TECHSTORE_ADMIN_PASSWORD` from the configuration.
- ☐ Never run two brand-new instances against an empty database at the same moment (both could try to create the first Admin).

### Cookies and keys
- ☐ **Encrypt the Data Protection key ring** (certificate or key vault). On Linux the framework writes the keys as unencrypted XML (mode 600) and logs a warning. Protect the `keys` volume until then.
- ☐ With more than one API instance, share one key ring between them; otherwise sessions and CSRF tokens differ per instance.

### Database
- ☐ Keep the database off the public network; allow only the API and the migration job to connect.
- ☐ Decide whether the migration user should be a **non-superuser owner** role. Today the owner is the PostgreSQL container's bootstrap superuser (used only by `migrate`). A dedicated owner role only matters if migrations must be isolated from the cluster.
- ☐ **Schedule backups** and test restores (`README.md` → "Backup and restore"). There are no automatic backups. Protect backup files: they contain accounts and password hashes.
- ☐ Plan major-version upgrades of PostgreSQL (the image is pinned to 17; a data directory from one major version cannot be opened by another without dump and restore).
- ☐ If a feature must delete rows from a table, add a `GRANT DELETE` line for it in `backend/database/Provisioning/app-role.sql`, deliberately.
- ☐ Apply migrations as a deliberate deployment step. `migrate` runs on every `up`; review each new migration for destructive changes first (the stack test fails if one appears).

### Operations
- ☐ Send container logs to a place you monitor. The stack's logs contain no passwords, cookies or tokens (tested); decide what else your log pipeline should keep or hide.
- ☐ Measure the cost of the per-request security-stamp check under realistic load (NFR4); raise `Auth:SessionRevalidationSeconds` only if it matters (values above 60 log a warning).
- ☐ Keep base images (`dotnet/aspnet`, `nginx-unprivileged`, `postgres`) and NuGet/npm packages updated; rebuild with `docker compose build --no-cache`. Dependabot proposes these updates weekly; CI fails on High or Critical known vulnerabilities.
- ☐ Provide a way to recover access if the only Admin is locked out or forgets the password (see section 4: there is no tool yet).

---

## 3. Configuration to review

| Setting | Default | Check |
|---|---|---|
| `Auth:SessionMinutes` | 480 | Idle timeout fits company policy |
| `Auth:SessionMaxHours` | 12 | Absolute limit fits company policy |
| `Auth:SessionRevalidationSeconds` | 0 | Keep at most 60; 0 checks every request |
| `Auth:RateLimit:LoginPerClientPerMinute` | 10 | |
| `Auth:RateLimit:LoginGlobalPerMinute` | 100 | Lower it to suit a small team |
| `Auth:RequireSecureCookie` | secure outside Development/Testing | Must stay unset (secure) in a real deployment |
| `Auth:AllowInsecureCookiesForLocalHttp` | `false` | Must stay `false` in a real deployment |
| `Auth:TrustedProxies` | empty (header ignored) | Exactly the proxy that overwrites `X-Forwarded-For` |
| `DataProtection:KeysPath` | image sets `/keys` | A persistent, protected volume |

(Full descriptions: `api-contract.md` §10.8.)

## Verify after deploying

- ☐ A response from `/api/...` has `Cache-Control: no-store` and the security headers; the static pages have the Content-Security-Policy.
- ☐ The session cookie is `HttpOnly; Secure; SameSite=Strict`.
- ☐ 11 quick wrong sign-ins from one machine return `429`, and a forged `X-Forwarded-For` does not change that.
- ☐ `docker compose ps` (or your orchestrator) shows every service healthy and `migrate` completed.
- ☐ The API connects as `techstore_app`, and `techstore_app` is not a superuser.
- ☐ A restore from your latest backup works in a disposable environment.
- ☐ Logs and images contain no passwords, cookies or tokens.

---

## 4. Known limitations and deferred work

- **No way to manage accounts after the first Admin.** There are no screens, endpoints or command-line tool to create employees, change roles, unlock accounts or reset passwords. Direct SQL account provisioning is used **only** by isolated tests (`compose-e2e.sh`) and is not a supported way to manage accounts. A password-reset command-line tool and a user-management screen are deferred. ⏳
- **Multi-factor authentication and single sign-on.** ⏳
- **Passwords as environment variables** (visible to `docker inspect`), **unencrypted key ring**, **no automatic backups**, **superuser migration owner**: see section 2.
- **`docker compose down -v` deletes all data** (database and cookie keys).
- **The local http cookie exception** is guarded by the loopback port binding, a startup warning, and the API's refusal to start with it outside Development/Testing unless both settings are present. It cannot stop someone from editing `compose.yaml` to publish the port, which is why section 2 forbids copying those lines.
- **Docker Desktop for macOS** shows every local browser to nginx as the same gateway address, so they share one login allowance. This does not occur on a Linux server.
- **A single host.** No replicas, high availability, or zero-downtime deploys.
- **Continuous deployment and cloud deployment.** ⏳ (GitHub Actions continuous integration, secret scanning, dependency audits, Dependabot and the `main` ruleset are in place; see the README.)
