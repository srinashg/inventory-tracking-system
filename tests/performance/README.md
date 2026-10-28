# Performance and reliability tooling (Step 14)

An **isolated, disposable** environment for measuring TechStore: a PostgreSQL 17 container of its own (port 55432) plus a Release build of
the API in the `Testing` environment (port 5611). It never touches the development database (5432), the Docker Compose stack (8080) or the
test databases. Everything it creates lives in the git-ignored folder `tests/performance/.results/`.

Needs Docker, `psql`, the .NET SDK and `dotnet-ef`, Python 3, and k6 (`brew install k6`).

| Command | What it does |
|---|---|
| `tests/performance/perf.sh up` | start the database container (starts Docker Desktop if needed) |
| `tests/performance/perf.sh fixtures` | build the template database: migrations + 10,000 products + 1,000 orders; validate; print the checksum |
| `tests/performance/perf.sh reset` | recreate the run database from the template (before every iteration) |
| `tests/performance/perf.sh api start` / `api stop` | run the API against the run database |
| `tests/performance/perf.sh sessions 10:sales 2:warehouse` | sign in independent sessions through the real login (paced under the rate limit) |
| `tests/performance/perf.sh drafts 500 2` | add fresh Draft orders (ids 1001+) for reservation runs |
| `tests/performance/perf.sh run smoke.js` | run a k6 script |
| `tests/performance/perf.sh invariants` | check the stock invariants from the database |
| `tests/performance/perf.sh smoke` | everything above in one go, on one session per role |
| `tests/performance/perf.sh info` | record machine, runtime and configuration details |
| `tests/performance/perf.sh down` | remove the container, its volume and every file the tool created |
| `tests/performance/perf.sh guard-test` | prove that the safety guards refuse wrong targets |

Layout: `sql/` (fixtures, validation, invariants, drafts), `k6/` (shared library and scripts), `sessions.py` (logins), `perf.sh`.
Differences from production: `Testing` environment (plain http, test accounts), no nginx proxy, no container limits.
Benchmarks arrive in later steps; results are never committed.
