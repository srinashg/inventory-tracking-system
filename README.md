# TechStore: Inventory & Order Management

TechStore is an internal web application for an electronics retailer. Employees use it to keep the product catalog, track how many units of each product are in each warehouse, take customer orders, and move those orders through picking, packing and shipping, **without ever selling stock that does not exist**.

- [Overview and the problem it solves](#overview-and-the-problem-it-solves)
- [Features and roles](#features-and-employee-roles)
- [Technology and architecture](#technology-and-architecture)
- [Project structure](#project-structure)
- [Quick start with Docker Compose](#quick-start-with-docker-compose)
- [Configuration and secrets](#configuration-and-secrets)
- [Day-to-day Docker Compose commands](#day-to-day-docker-compose-commands)
- [How the database is created, migrated and kept](#how-the-database-is-created-migrated-and-kept)
- [Backup and restore](#backup-and-restore)
- [Running without Docker (development)](#running-without-docker-development)
- [Running the tests](#running-the-tests)
- [Working with Git and GitHub](#working-with-git-and-github)
- [Troubleshooting](#troubleshooting)
- [Known limitations and security notes](#known-limitations-and-security-notes)
- [More documentation](#more-documentation)

---

## Overview and the problem it solves

A retailer that sells the same laptop through several people at once can easily promise more units than it has. TechStore prevents that:

1. Every product has stock **on hand** and stock **reserved** in each warehouse; **available = on hand − reserved**.
2. When an order is **submitted**, its quantities are reserved in one database transaction. If any line is short, nothing is reserved and the employee sees a clear message.
3. Picking, packing and shipping move the order forward. **Shipping** removes the units from stock for good; **canceling** releases the reservation.
4. Two people submitting orders for the last unit at the same moment cannot both succeed; the database settles it (see `docs/services.md`).

Example: receive 10 laptops → order 2 → submit (2 reserved, 8 available) → pick → pack → ship (8 on hand, 8 available). Ordering 15 when only 8 are available is refused and changes nothing.

## Features and employee roles

| Page | What employees do there |
|---|---|
| Dashboard | Totals and low-stock alerts |
| Products | List, add, edit, deactivate and reactivate products (unique SKUs, prices to the cent) |
| Inventory | See stock per warehouse; receive stock; adjust stock with a reason |
| Orders | Create a draft order, submit (reserve), cancel, find and view any order |
| Fulfillment | Work the queue: pick, pack, ship |

Everyone must sign in. What a signed-in employee may *do* depends on the role (the API enforces this; the screens simply hide what you may not use):

| Role | May do |
|---|---|
| **Admin** | Everything |
| **Sales** | View everything; create, submit and cancel orders |
| **Warehouse** | View everything; receive and adjust stock; pick, pack and ship orders |

Anonymous requests get `401`; signed-in users who lack permission get `403`. The endpoint-by-endpoint matrix is in `docs/api-contract.md` (section 2.1).

## Technology and architecture

| Layer | Technology |
|---|---|
| Frontend | React 19, TypeScript, Vite, React Router; Vitest and React Testing Library; Playwright |
| Backend | C#, ASP.NET Core (.NET 10), Entity Framework Core, ASP.NET Core Identity (cookie sessions, CSRF protection) |
| Database | PostgreSQL 17 |
| Tests | xUnit (backend), Vitest (frontend), Playwright (browser), shell scripts (containers) |
| Packaging | Docker and Docker Compose; nginx serves the built frontend |

**Every request follows the same path:**

```
Browser (React) → Controller → Service → DbContext (EF Core) → PostgreSQL
                                                                   │
Browser (React) ← Controller ← Service ← DbContext ←── result ─────┘
```

- **React** only sends requests and shows answers. Stock numbers and order rules are decided by the backend.
- **Controllers** (`backend/controllers`) receive the request and return the response; they hold no business rules.
- **Services** (`backend/services`) hold the rules: validation, reservations, status changes, transactions.
- **`DbContext`** (`backend/database`) turns those operations into SQL; `SaveChangesAsync()` and explicit transactions make them permanent or roll them back.

**The Docker Compose stack** has four services on one private network. Only `web` is reachable, and only from your own computer:

```
your browser ──http://localhost:8080──▶ web (nginx) ── /api, /health ──▶ api (ASP.NET Core) ──▶ db (PostgreSQL)
                                          serves the React build              ▲
                                                                  migrate (runs once, then exits)
```

| Service | Job |
|---|---|
| `db` | PostgreSQL, data kept in the named volume `pgdata` |
| `migrate` | Runs once at every start as the database owner: applies missing EF Core migrations, then creates or refreshes the limited database user the API uses |
| `api` | The ASP.NET Core API; connects as the limited user `techstore_app`; keeps its cookie keys in the named volume `keys` |
| `web` | nginx: serves the React app (any URL such as `/orders` works, even after a refresh) and forwards `/api` and `/health` to `api` |

The browser talks only to `web`, so the app and the API share **one origin**: the session cookie works as designed and no CORS setup is needed.

## Project structure

```
.
├── README.md                    this file
├── compose.yaml                 the Docker Compose stack
├── .env.example                 settings template (copy to .env; .env is never committed)
├── TechStore.slnx               the .NET solution
├── backend/
│   ├── Dockerfile               API image (stages: build, migration-script, migrate, runtime)
│   ├── controllers/             ASP.NET Core API: controllers, middleware, Program.cs
│   ├── services/                business rules (orders, reservations, inventory, fulfillment, ...)
│   ├── auth/                    accounts, roles, policies, login, first-Admin setup
│   ├── database/                EF Core entities, DbContext, migrations, Provisioning/ (limited-user SQL)
│   └── dto/                     request and response shapes
├── frontend/
│   ├── Dockerfile               web image (Node build, then non-root nginx)
│   ├── nginx/                   nginx configuration used in the web image
│   ├── src/                     React app (pages, components, api client, auth)
│   ├── e2e/                     Playwright tests that start their own API and database
│   └── e2e-compose/             Playwright tests against the real Docker Compose stack
├── tests/
│   ├── TechStore.Services.Tests/   xUnit: business rules against PostgreSQL
│   ├── TechStore.Auth.Tests/       xUnit: accounts, passwords, sessions
│   ├── TechStore.Api.Tests/        xUnit: the HTTP API end to end, and database privileges
│   └── containers/                 shell tests for the images and the Compose stack
└── docs/                        requirements, architecture, entities, services, API contract, schema, security checklist
```

---

## Quick start with Docker Compose

This is the recommended way to run TechStore. Nothing but Docker is needed.

### Prerequisites

- **Docker** with **Docker Compose v2** (Docker Desktop on macOS/Windows, or Docker Engine plus the Compose plugin on Linux). Check with `docker compose version`.
- **Free port 8080** on your computer (you can change it, see [Configuration](#configuration-and-secrets)).
- `openssl` to generate random passwords (included with macOS and Linux; on Windows use WSL, Git Bash, or any other way to make a long random string of letters and digits).

### First-time setup

Get the code first (see [Working with Git and GitHub](#working-with-git-and-github)):

```bash
git clone https://github.com/srinashg/inventory-tracking-system.git
cd inventory-tracking-system
```

Then, from the repository root:

```bash
cp .env.example .env
```

Create two different random database passwords and put them in `.env`:

```bash
sed -i.bak "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=$(openssl rand -hex 24)/; s/^TECHSTORE_APP_DB_PASSWORD=.*/TECHSTORE_APP_DB_PASSWORD=$(openssl rand -hex 24)/" .env && rm .env.bak
```

Then open `.env` in an editor and fill in the **first administrator** (you choose these; they are not generated):

```
TECHSTORE_ADMIN_EMAIL=you@example.com
TECHSTORE_ADMIN_PASSWORD=<your password>
```

The admin password must have at least 12 characters, at least 5 different characters, must not contain a common word (`password`, `passw0rd`, `qwerty`, `letmein`, `12345678`) and must not contain the part of your email before the `@`.

Start everything:

```bash
docker compose up --build -d
```

The first run downloads images and builds the API and web images (a few minutes). When it finishes, check:

```bash
docker compose ps
```

You should see `db`, `api` and `web` as `healthy` and `migrate` as `Exited (0)`. (`web` may say `health: starting` for a few seconds; run the command again.)

### Open the application

- **App:** http://localhost:8080 (sign in with the Admin email and password from `.env`)
- **API health:** http://localhost:8080/health, or from a terminal:

  ```bash
  curl http://localhost:8080/health
  ```

  `Healthy` means the API is running **and** can reach PostgreSQL. (The web server's own check is http://localhost:8080/healthz.)

### After the first sign-in: remove the Admin settings

The Admin settings are only used when no Admin exists. Once you have signed in, **empty the two values** in `.env` (keep the lines, leave the text after `=` blank) and apply it:

```bash
docker compose up -d
```

The API container is recreated without the password; nothing else changes, and you can still sign in. If you leave the values, the API ignores them and logs a warning (never with the values).

---

## Configuration and secrets

All settings come from the `.env` file in the repository root, which Docker Compose reads automatically. **`.env` holds secrets: it is listed in `.gitignore`, never commit it, never paste it into chats or tickets.** `.env.example` is the committed template and contains no secrets.

| Setting | Required | Meaning |
|---|---|---|
| `POSTGRES_PASSWORD` | yes | Password of the database owner (used by the database and the `migrate` container). Set when the database is **first created**; changing it later does not change the existing database |
| `TECHSTORE_APP_DB_PASSWORD` | yes | Password of the limited database user `techstore_app` that the API uses. Use a different value from the one above. The `migrate` container re-applies it on every start, so you *can* change it later |
| `TECHSTORE_ADMIN_EMAIL`, `TECHSTORE_ADMIN_PASSWORD` | first start only | Creates the first Admin when none exists. Without them on a brand-new database the API refuses to start and says which settings to provide |
| `TECHSTORE_ADMIN_NAME` | no | Display name for the first Admin (default `Administrator`) |
| `TECHSTORE_WEB_PORT` | no | Port on your computer (default `8080`) |
| `TECHSTORE_SUBNET`, `TECHSTORE_WEB_IP` | no | Docker network (default `172.28.0.0/24`) and the fixed address of the web container (default `172.28.0.10`), which the API trusts for `X-Forwarded-For`. Change both together only if the subnet clashes with another network |
| `POSTGRES_USER`, `POSTGRES_DB` | no | Defaults `techstore` and `techstore`. Set before the first start only |

How secrets are handled:

- They reach containers as environment variables at run time. They are **not** baked into any image and are never written to logs (the test suite checks both).
- Anyone who can run `docker inspect` on your computer can read them. That is acceptable for a local machine and is listed under [limitations](#known-limitations-and-security-notes).
- Use `chmod 600 .env` on a shared computer.

### Managing employee accounts

The first Admin is created from the settings above. **There are not yet screens, endpoints or a command-line tool for creating more employees, changing roles or resetting passwords**; these are planned for a later milestone. Until then, treat TechStore as having the one Admin account.

The Sales and Warehouse accounts you see in the automated tests are created with **direct SQL**, copying an existing password hash, **only inside isolated, throw-away test stacks** (`tests/containers/compose-e2e.sh`). That is a testing shortcut. **It is not a recommended way to manage employee accounts** and must not be used on a real installation. (The ASP.NET Core Identity password hasher is the only thing that should ever produce a password hash.)

Security behavior you will notice: five wrong passwords lock an account for 15 minutes; more than 10 sign-in attempts per minute from one client get `429 Too Many Requests`; a session ends after 8 hours without activity or 12 hours after sign-in.

---

## Day-to-day Docker Compose commands

Run all of these from the repository root.

| Task | Command |
|---|---|
| Start (build first if needed) | `docker compose up --build -d` |
| Start without rebuilding | `docker compose up -d` |
| See status and health | `docker compose ps` |
| Follow the API log | `docker compose logs -f api` |
| Follow all logs | `docker compose logs -f` |
| Stop, keep everything | `docker compose stop` |
| Start again after `stop` | `docker compose start` |
| Restart | `docker compose restart` (or `docker compose restart api`) |
| Rebuild after changing code | `docker compose up --build -d` |
| Rebuild from scratch (no cache) | `docker compose build --no-cache && docker compose up -d` |
| Remove containers, **keep data** | `docker compose down` |
| Run migrations and role provisioning by hand | `docker compose run --rm migrate` |
| Back up / restore the database | see [Backup and restore](#backup-and-restore) |

> ### ⚠️ `docker compose down -v` PERMANENTLY DELETES YOUR DATA
>
> The `-v` option removes the named volumes **`pgdata` (the whole database: products, stock, orders, employee accounts) and `keys` (the cookie keys, so everyone is signed out)**. It cannot be undone. Use plain `docker compose down` to stop and remove containers while keeping the data. Take a backup first if there is any doubt.
>
> The same applies to removing the volumes by hand (`docker volume rm`, `docker volume prune`, `docker system prune --volumes`). The volumes are named `techstore_pgdata` and `techstore_keys`.

---

## How the database is created, migrated and kept

**Creation.** On the very first start the `db` container creates an empty database from `POSTGRES_DB`, owned by `POSTGRES_USER`, with the password from `POSTGRES_PASSWORD`.

**Migrations (EF Core).** The tables are defined by migrations in `backend/database/Migrations`. When the image is built, `dotnet ef migrations script --idempotent` turns them into one SQL script. The `migrate` container runs it on every `docker compose up`, as the database owner, before the API starts. The script checks the `__EFMigrationsHistory` table first and only runs migrations that are missing, so **running it again changes nothing and never deletes data**. If it fails, `migrate` exits with an error and the API does not start. (A test also fails if a migration ever contains `DROP`, `TRUNCATE` or `DELETE FROM`, so such a change gets a human review.)

**The limited database user.** After the migrations, `migrate` runs `backend/database/Provisioning/app-role.sql`, which creates (or refreshes) the PostgreSQL user `techstore_app` that the API connects as. It cannot create or drop tables, change the schema, create roles or databases, or use superuser features. It may read, insert and update rows, use sequences, and delete only from `user_roles`; it cannot touch the migration history. Default privileges make tables added by future migrations usable at once. If a future feature must delete rows from some table, add a `GRANT DELETE` line for it in that file. The script is safe to repeat and re-applies the password from `TECHSTORE_APP_DB_PASSWORD`.

**The first Admin.** When the API starts it makes sure the three roles exist and, if no Admin exists, creates one from `TECHSTORE_ADMIN_EMAIL` and `TECHSTORE_ADMIN_PASSWORD` using ASP.NET Core Identity's password hasher. If an Admin already exists those settings are ignored.

**Persistence.**

| Volume | Holds | Survives |
|---|---|---|
| `pgdata` | all PostgreSQL data | restarts, `stop`, `down`, rebuilds, image updates |
| `keys` | the ASP.NET Core Data Protection key ring (signs and encrypts session and CSRF cookies) | the same, so people stay signed in across restarts |

Both are deleted only by `down -v` or removing the volumes by hand. PostgreSQL is pinned to major version 17: a data directory created by 17 cannot be opened by a newer major version without a dump and restore.

---

## Backup and restore

`docker compose down -v` or a lost disk destroys the data, and **there are no automatic backups**, so take one regularly. A backup contains *everything*: products, stock, orders **and employee accounts with their password hashes**. Store it somewhere safe and private, and delete copies you no longer need.

### Back up (the stack can keep running)

```bash
docker compose exec db pg_dump -U techstore -Fc -f /tmp/techstore.dump techstore
docker compose cp db:/tmp/techstore.dump ./techstore-backup.dump
docker compose exec db rm /tmp/techstore.dump
```

This creates `techstore-backup.dump` in the current folder. (It writes the file inside the container and copies it out, so it works the same on macOS, Linux and Windows. Do not use `> file` redirection with `pg_dump -Fc`: some shells corrupt binary output.) If you changed `POSTGRES_USER` or `POSTGRES_DB`, use your values instead of `techstore`.

### Restore

> **A restore replaces what is in the database with the contents of the backup.** Everything entered after the backup is lost, including employee accounts. Restore only into the stack you intend to overwrite, and take a fresh backup of it first if you may need its current contents.

Run from the repository root, with the stack started (`docker compose up -d`), for the stack you want to restore **into**:

```bash
docker compose cp ./techstore-backup.dump db:/tmp/restore.dump
docker compose stop web api
docker compose exec db pg_restore -U techstore -d techstore --clean --if-exists --single-transaction --exit-on-error /tmp/restore.dump
docker compose exec db rm /tmp/restore.dump
docker compose run --rm migrate
docker compose start api web
```

What each step does:

1. Copies the backup into the database container.
2. Stops the web and API containers, so nothing writes during the restore.
3. Restores the backup. `--single-transaction --exit-on-error` makes it **all or nothing**: if the file is damaged or anything fails, the database is left exactly as it was. `--clean --if-exists` replaces the existing tables.
4. Removes the copy.
5. Runs the migration container again. If the backup came from an older version of the application, this applies the newer migrations; it also re-applies the limited database user's permissions and password from your current `.env`. It is safe to repeat.
6. Starts the API and web again. Check http://localhost:8080/health, then sign in. Sign in with the account that existed **at the time of the backup**.

The database passwords in your `.env` are not part of a backup (they belong to the PostgreSQL server, not to its data), so restoring into a new installation with different `.env` passwords works: the stack keeps its own database passwords and gets the old records and accounts. Everyone is signed out if you restore into a stack with a different `keys` volume.

This procedure is tested by `bash tests/containers/backup-restore.sh` on disposable stacks only (see [Running the tests](#running-the-tests)). **Never practise a restore on a database you care about**; try it on a throw-away copy of the project first.

---

## Running without Docker (development)

Use this to work on the code with debugging and hot reload. You need:

- **.NET 10 SDK**, the **`dotnet-ef`** tool (`dotnet tool install --global dotnet-ef --version 10.0.14`; make sure `~/.dotnet/tools` is on your `PATH`)
- **Node.js 22 or newer** with npm
- A **PostgreSQL 17** server on `localhost:5432`. The quickest is a throw-away container that trusts local connections (**development only**, never for real data):

  ```bash
  docker run -d --name techstore-postgres -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=techstore -p 127.0.0.1:5432:5432 postgres:17
  ```

Then, from the repository root:

```bash
# 1. Create the tables (connects to the "techstore" database on localhost; override with TECHSTORE_DB)
dotnet ef database update --project backend/database

# 2. Tell the API who the first Admin is (stored outside the repository, never logged)
dotnet user-secrets set "Auth:InitialAdmin:Email" "you@example.com" --project backend/controllers
dotnet user-secrets set "Auth:InitialAdmin:Password" "<at least 12 characters>" --project backend/controllers

# 3. Start the API on http://localhost:5111
dotnet run --project backend/controllers
```

In a second terminal:

```bash
cd frontend
npm ci
npm run dev
```

Open http://localhost:5173. The Vite dev server forwards `/api` and `/health` to the API (set `VITE_API_TARGET` to use another API address). Once the Admin exists you can run `dotnet user-secrets clear --project backend/controllers`.

---

## Running the tests

Backend and browser tests need a PostgreSQL server on `localhost:5432` that accepts the `postgres` user without a password (the development container above does). Each suite creates and uses its own databases, so **your `techstore` development database is never touched**: `techstore_test`, `techstore_auth_test`, `techstore_api_test`, `techstore_api_priv_test` and `techstore_e2e`. Override the connection with `TECHSTORE_TEST_DB`, `TECHSTORE_AUTH_TEST_DB`, `TECHSTORE_API_TEST_DB` or `TECHSTORE_API_PRIV_TEST_DB` if yours differs.

### Backend (xUnit)

```bash
dotnet test TechStore.slnx
```

Runs the three projects: business rules (`TechStore.Services.Tests`), accounts and sessions (`TechStore.Auth.Tests`) and the HTTP API plus database-privilege tests (`TechStore.Api.Tests`). Run one project with `dotnet test tests/TechStore.Api.Tests`, or a subset with `--filter`.

### Frontend (Vitest and React Testing Library)

```bash
cd frontend
npm ci
npm test            # unit and component tests, API mocked
npm run lint
npm run build       # type-checks and builds
```

### Browser tests (Playwright)

```bash
cd frontend
npx playwright install chromium      # once
npm run e2e
```

Needs `psql`, the .NET SDK and `dotnet-ef` on your `PATH`. It starts its own API (in the `Testing` environment, with random-password test accounts) and web server on ports 5290, 5291, 5273 and 5274, against the database `techstore_e2e`, which it **drops and recreates** on every run. It covers login and logout, protected routes, role permissions, `401`/`403`, CSRF, session revocation after logout, session expiry, the login rate limit, and the full inventory and order workflow. The expiry test waits real time, so expect about two minutes.

### Container tests (Docker)

These build the images and run the real Compose stack under their own project names and with their own random secrets, then delete everything they created. They never use your `.env`, your running stack or your volumes. They need Docker, `curl`, `python3` and `openssl`; the last one also needs the Playwright browser installed as above.

```bash
bash tests/containers/web-image.sh     # web image: routing, headers, proxying, X-Forwarded-For, cookies, non-root
bash tests/containers/compose-stack.sh # whole stack: startup, migrations, limited DB user, login, persistence, secrets
bash tests/containers/compose-e2e.sh   # whole stack in a real browser, before and after recreating every container
bash tests/containers/backup-restore.sh # backup and restore, in place and into a brand-new stack
```

Each prints `passed: N   failed: M` and exits non-zero on failure.

---

## Working with Git and GitHub

The code is in the public repository https://github.com/srinashg/inventory-tracking-system. The default branch is `main`.

### Get the code and configure it

```bash
git clone https://github.com/srinashg/inventory-tracking-system.git
cd inventory-tracking-system
cp .env.example .env
```

Then fill in `.env` and start the application by following the [Quick start](#quick-start-with-docker-compose) (or [development without Docker](#running-without-docker-development)); those sections are the single source of setup instructions.

- `.env.example` is a committed **template with blank values**. Never put real values in it.
- `.env` holds your real secrets, exists only on your computer and is ignored by Git. Check with `git check-ignore -v .env` (it prints the rule that ignores it) and make sure `git status` never lists it.
- For development without Docker, keep the first Admin's credentials in `dotnet user-secrets` (stored outside the repository), as shown above.

### Everyday commands

```bash
git status                       # what changed (look for files you did not mean to touch)
git switch -c feature/short-name # start a branch for your change (never work directly on main)
git diff                         # review your edits
git add path/to/file1 path/to/file2   # stage specific files
git diff --cached                # review exactly what will be committed
git commit -m "Short, clear summary of the change"
git push -u origin feature/short-name # first push of the branch (afterwards just: git push)
git switch main && git pull      # get the latest main
```

### Contributing changes safely

1. Update `main` (`git switch main && git pull`), then create a **feature branch** from it.
2. Make a small, focused change. Add or update tests, and update the documentation if behavior changes.
3. Run the relevant tests from [Running the tests](#running-the-tests) before pushing.
4. Review `git status` and `git diff --cached`, then commit and push the branch.
5. Open a **pull request** into `main` on GitHub (or with `gh pr create`), describe what and why, and ask for a review. Do not push straight to `main`.
6. If you are not a collaborator on the repository, fork it first, push your branch to your fork and open the pull request from there.

### Not committing secrets, generated files or local settings

`.gitignore` already excludes `.env` files, key and certificate files, ASP.NET Core Data Protection keys, database backups (`*.dump`), build output (`bin/`, `obj/`, `node_modules/`, `dist/`), test output (`test-results/`, `playwright-report/`; these can hold saved login sessions) and per-machine files (`.DS_Store`, `.claude/settings.local.json`). Habits that keep it that way:

- Stage files by name, not with a blind `git add -A`, and read `git diff --cached` before every commit.
- Keep secrets in `.env`, environment variables or `dotnet user-secrets`, never in code, tests, `compose.yaml` or the docs. Tests generate random passwords instead of using real ones.
- Scan before you push. This runs the open-source scanner Gitleaks from its official image over the working folder (it needs Docker and prints no secret values):

  ```bash
  docker run --rm -v "$PWD:/repo:ro" ghcr.io/gitleaks/gitleaks:latest dir /repo --redact
  ```

  Use `... git /repo --redact` instead to scan the commit history.
- **If a secret was committed:** treat it as exposed. Change (rotate) the password or key first. If the commit has **not** been pushed, undo it with `git reset --soft HEAD~1`, remove the secret, and commit again. If it **has** been pushed to this public repository, rotating the secret is the real fix: deleting the file in a later commit does not remove it from history.
- Do not add a database backup, a `.env` file or anything from `test-results/` to a commit, even temporarily.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Cannot connect to the Docker daemon` | Docker is not running. Start Docker Desktop (macOS: `open -a Docker`) and wait until it says it is running |
| `required variable POSTGRES_PASSWORD is missing` (or `TECHSTORE_APP_DB_PASSWORD`) | `.env` does not exist or the value is blank. Redo [first-time setup](#first-time-setup) |
| `port is already allocated` / `address already in use` on 8080 | Something else uses the port. Set `TECHSTORE_WEB_PORT=8081` (or another free port) in `.env`, run `docker compose up -d`, and use that port in the URL |
| `Pool overlaps with other one on this address` | Another Docker network uses `172.28.0.0/24`. In `.env` set both, for example, `TECHSTORE_SUBNET=172.29.0.0/24` and `TECHSTORE_WEB_IP=172.29.0.10` |
| `api` keeps stopping; log says to provide `Auth:InitialAdmin` settings | Brand-new database and no Admin settings. Fill `TECHSTORE_ADMIN_EMAIL` and `TECHSTORE_ADMIN_PASSWORD` in `.env`, then `docker compose up -d` |
| The log says the Admin password is "too easy to guess" | It breaks a password rule (length, variety, common word, or contains your email name). Choose another, then `docker compose up -d` |
| `migrate` fails with `password authentication failed` | You changed `POSTGRES_PASSWORD` after the database was created; the database still has the old one. Put the old value back. (Starting over with a new password means deleting the data with `down -v`: only do that if you accept losing it) |
| `web` or the page shows `502`/`504` | The API is not up yet or crashed. `docker compose ps`, then `docker compose logs api`. It recovers by itself once the API is healthy |
| Login says `Invalid email or password.` | Wrong credentials. The message is deliberately the same for an unknown email, a wrong password and a locked account (five failures lock it for 15 minutes) |
| Login says `Too many sign-in attempts` (`429`) | More than 10 attempts in a minute from your address. Wait a minute |
| "Your session has ended" appears | The session expired (8 hours idle or 12 hours total), or you signed out in another browser (sign-out ends all of a user's sessions). Sign in again |
| Everyone was signed out after a restart | The `keys` volume was deleted (for example by `down -v`). Sign in again |
| Running the API directly: `relation "..." does not exist` | The migrations were not applied. Run `dotnet ef database update --project backend/database` |
| `dotnet ef` not found | `dotnet tool install --global dotnet-ef --version 10.0.14`, and add `~/.dotnet/tools` to your `PATH` |
| Tests cannot connect to the database | PostgreSQL is not running on `localhost:5432`, or requires a password. See [Running the tests](#running-the-tests) |
| A Docker build fails halfway | Re-run it; if it keeps failing, `docker compose build --no-cache` and check your internet connection (images and packages are downloaded) |

When in doubt, `docker compose ps` and `docker compose logs <service>` say what is wrong.

---

## Known limitations and security notes

**Differences between the local Compose stack and a real deployment**

| | Local stack (this repository) | Real deployment |
|---|---|---|
| Transport | Plain `http`, published on `127.0.0.1` only | Must use **HTTPS** (TLS in front), plus HSTS |
| Session cookie | Not `Secure`, allowed only by the explicit local-only settings in `compose.yaml` (`Auth__RequireSecureCookie=false` and `Auth__AllowInsecureCookiesForLocalHttp=true`); the API logs a warning at start | `Secure` (the default: the API **refuses to start** with insecure cookies unless both settings are present). **Do not copy those two lines** |
| Client address | The web container's fixed address is the one proxy the API believes for `X-Forwarded-For` | List your real proxy in `Auth:TrustedProxies`; make it overwrite the header; otherwise the login rate limit sees only the proxy |
| Secrets | `.env` and container environment variables | A secret store or file-based secrets |
| Data Protection keys | Unencrypted files on the `keys` volume | Encrypt them (certificate or key vault) and protect the volume |
| Database users | Owner (migrations) and limited `techstore_app` (API) | Same, plus network isolation and backups |
| Test accounts | Do not exist (only in the `Testing` environment) | Do not exist |
| Hostnames | `AllowedHosts` is `*` | Set the real host names |

The full list is in [`docs/deployment-security-checklist.md`](docs/deployment-security-checklist.md).

**Limitations of this version**

- No screens, endpoints or command-line tool to create employees, change roles, unlock accounts or reset passwords (only the first Admin is created). Account provisioning by direct SQL exists only in isolated tests and is **not** a supported way to manage accounts.
- No multi-factor authentication.
- No automatic backups. `docker compose down -v` deletes all data. Take backups yourself (see [Backup and restore](#backup-and-restore)).
- On Docker Desktop for macOS, every browser on your computer reaches `web` from the same Docker gateway address, so they share one login rate-limit allowance.
- A single host: no replicas, no high availability.
- Scope: one warehouse per order; shipping is a simulated confirmation; no payments, returns, transfers between warehouses or customer storefront (see `docs/requirements.md`).
- No CI/CD pipeline or cloud deployment yet.

## More documentation

| Document | Contents |
|---|---|
| [`docs/requirements.md`](docs/requirements.md) | Functional and nonfunctional requirements with acceptance criteria |
| [`docs/architecture.md`](docs/architecture.md) | What each user request does, order lifecycle, the employee interface |
| [`docs/entities.md`](docs/entities.md) | The core objects and their roles |
| [`docs/diagrams/schema.dbml`](docs/diagrams/schema.dbml) | Database schema |
| [`docs/services.md`](docs/services.md) | Service logic, transactions, concurrency, testing constraints |
| [`docs/api-contract.md`](docs/api-contract.md) | Every endpoint, its permissions, requests, responses and errors |
| [`docs/deployment-security-checklist.md`](docs/deployment-security-checklist.md) | What to decide and configure before a real deployment |
