#!/usr/bin/env bash
# TechStore performance tooling (Step 14B). One entry point to create, use and destroy an ISOLATED performance environment:
#   a disposable PostgreSQL 17 container (own port, own random passwords) + a Release build of the API in the "Testing" environment.
# It never reads .env, never uses the development connection string, and refuses every database whose name does not start with
# "techstore_perf_". Usage: tests/performance/perf.sh help
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
RESULTS="$HERE/.results" # git-ignored: passwords, sessions, logs, benchmark output

# ---- Fixed targets. Nothing in this script can point anywhere else. ---------------------------------------------------------
CONTAINER="techstore-perf-db"
PG_HOST="127.0.0.1"
PG_PORT="55432"          # the development database is on 5432 and the Compose stack on 8080: neither is touched
API_HOST="127.0.0.1"
API_PORT="5611"          # the development API is on 5111
TEMPLATE_DB="techstore_perf_template"
RUN_DB="techstore_perf_run"
APP_ROLE="techstore_perf_app"
IMAGE="postgres:17"
PACE_SECONDS="6.5"       # login pacing: the sign-in limit is 10 per minute per client address, and it is NOT raised for these runs

# ---- Start from a clean environment: nothing the developer exported can redirect the tools. ---------------------------------
for name in $(env | cut -d= -f1 | grep -E '^(PG[A-Z]*|ConnectionStrings__.*|ASPNETCORE_.*|DOTNET_ENVIRONMENT|TECHSTORE_.*|Auth__.*|K6_.*)$' || true); do
  unset "$name"
done
if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"; fi
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

die() { echo "perf: $*" >&2; exit 1; }
say() { echo "perf: $*"; }

# ---- Guards ----------------------------------------------------------------------------------------------------------------
assert_perf_db() { # a database name is acceptable only if it is one of ours
  [[ "$1" =~ ^techstore_perf_[a-z0-9_]+$ ]] || die "refusing database '$1': performance tooling only touches databases named techstore_perf_*"
}

load_secrets() {
  [ -f "$RESULTS/perf.env" ] || die "no performance environment yet: run '$0 up' first"
  # shellcheck disable=SC1091
  . "$RESULTS/perf.env"
  [ -n "${PERF_DB_PASSWORD:-}" ] && [ -n "${PERF_APP_PASSWORD:-}" ] && [ -n "${PERF_ACCOUNT_PASSWORD:-}" ] || die "perf.env is incomplete: run 'down' and 'up' again"
}

port_in_use() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

need_docker() {
  command -v docker >/dev/null || die "docker is not installed"
  if ! docker info >/dev/null 2>&1; then
    say "Docker is not running: starting Docker Desktop (allowed for the performance environment)"
    open -a Docker || die "could not start Docker Desktop"
    for _ in $(seq 1 90); do docker info >/dev/null 2>&1 && return 0; sleep 2; done
    die "Docker did not become ready within 3 minutes"
  fi
}

# psql against the perf container only. $1 is the database; the rest are psql arguments.
psql_perf() {
  local db="$1"; shift
  if [ "$db" != "postgres" ]; then assert_perf_db "$db"; fi
  PGPASSWORD="$PERF_DB_PASSWORD" psql -X -q -h "$PG_HOST" -p "$PG_PORT" -U postgres -d "$db" -v ON_ERROR_STOP=1 "$@"
}

container_running() { [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || true)" = "true" ]; }

api_pid() { [ -f "$RESULTS/api.pid" ] && cat "$RESULTS/api.pid" || true; }
api_is_ours() { # the pid file may be stale: only ever signal a process that really is our API
  local pid command; pid="$(api_pid)"; [ -n "$pid" ] || return 1
  command="$(ps -p "$pid" -o command= 2>/dev/null || true)" # (no "| grep -q": under pipefail it can report a false failure)
  case "$command" in *TechStore.Api.dll*) return 0 ;; *) return 1 ;; esac
}
database_exists() { [ "$(psql_perf postgres -tAc "SELECT count(*) FROM pg_database WHERE datname = '$1'")" = "1" ]; }

# ---- Commands --------------------------------------------------------------------------------------------------------------
cmd_up() {
  need_docker
  mkdir -p "$RESULTS" && chmod 700 "$RESULTS"
  if [ ! -f "$RESULTS/perf.env" ]; then
    ( umask 077
      { echo "PERF_DB_PASSWORD=$(openssl rand -hex 16)"; echo "PERF_APP_PASSWORD=$(openssl rand -hex 16)"; echo "PERF_ACCOUNT_PASSWORD=$(openssl rand -hex 16)"; } > "$RESULTS/perf.env" )
  fi
  load_secrets
  if docker inspect "$CONTAINER" >/dev/null 2>&1; then
    [ "$(docker inspect -f '{{index .Config.Labels "techstore.perf"}}' "$CONTAINER")" = "1" ] || die "a container named $CONTAINER exists but is not ours"
    container_running || docker start "$CONTAINER" >/dev/null
  else
    port_in_use "$PG_PORT" && die "port $PG_PORT is already in use by something else"
    # The password reaches the container through the environment, not the command line. pg_stat_statements lets later steps
    # attribute time to individual SQL statements; max_connections is PostgreSQL's own default, stated so it is on record.
    POSTGRES_PASSWORD="$PERF_DB_PASSWORD" docker run -d --name "$CONTAINER" --label techstore.perf=1 \
      -p "$PG_HOST:$PG_PORT:5432" -e POSTGRES_PASSWORD "$IMAGE" \
      -c shared_preload_libraries=pg_stat_statements -c max_connections=100 >/dev/null
  fi
  for _ in $(seq 1 60); do psql_perf postgres -c 'SELECT 1' >/dev/null 2>&1 && break; sleep 1; done
  psql_perf postgres -c 'SELECT 1' >/dev/null 2>&1 || die "PostgreSQL did not become ready"
  say "PostgreSQL 17 is ready on $PG_HOST:$PG_PORT (container $CONTAINER)"
}

cmd_fixtures() {
  load_secrets; need_docker
  container_running || die "the performance database is not running: run '$0 up'"
  say "building the API (Release)"
  dotnet build "$REPO/backend/controllers" -c Release --nologo -v q >/dev/null
  assert_perf_db "$TEMPLATE_DB"
  psql_perf postgres -c "DROP DATABASE IF EXISTS \"$TEMPLATE_DB\" WITH (FORCE)" -c "CREATE DATABASE \"$TEMPLATE_DB\""
  psql_perf "$TEMPLATE_DB" -c 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements'
  say "applying the EF Core migrations to $TEMPLATE_DB"
  ( cd "$REPO/backend/database" && TECHSTORE_DB="Host=$PG_HOST;Port=$PG_PORT;Database=$TEMPLATE_DB;Username=postgres;Password=$PERF_DB_PASSWORD" \
      dotnet ef database update >/dev/null )
  say "generating the fixtures"
  psql_perf "$TEMPLATE_DB" -f "$HERE/sql/01-fixtures.sql" >/dev/null
  # The limited application role, exactly as in the Docker stack (backend/database/Provisioning/app-role.sql), so the API runs with
  # the same database privileges as in production. The password goes through a pipe, never a command line.
  { echo "SELECT set_config('techstore.app_password', '$PERF_APP_PASSWORD', false);"
    echo "SELECT set_config('techstore.app_role', '$APP_ROLE', false);"
    cat "$REPO/backend/database/Provisioning/app-role.sql"; } | psql_perf "$TEMPLATE_DB" >/dev/null
  # The three test accounts are created once, inside the template, by starting the API against it. Every database cloned from the template
  # then already has them (same users, same security stamps), so signed-in sessions stay valid across "reset".
  say "creating the test accounts in the template"
  start_api "$TEMPLATE_DB"
  api_stop_quiet
  say "validating the fixtures"
  psql_perf "$TEMPLATE_DB" -f "$HERE/sql/02-validate-fixtures.sql" | tee "$RESULTS/fixture-validation.txt"
  psql_perf "$TEMPLATE_DB" -f "$HERE/sql/03-invariants.sql"
  grep 'dataset checksum' "$RESULTS/fixture-validation.txt" | awk -F'|' '{gsub(/ /, "", $2); print $2}' > "$RESULTS/dataset-checksum.txt"
  say "template ready: $TEMPLATE_DB (checksum $(cat "$RESULTS/dataset-checksum.txt"))"
}

cmd_reset() {
  load_secrets
  container_running || die "the performance database is not running: run '$0 up'"
  cmd_api_stop
  assert_perf_db "$RUN_DB"; assert_perf_db "$TEMPLATE_DB"
  database_exists "$TEMPLATE_DB" || die "no template yet: run '$0 fixtures'"
  psql_perf postgres -c "DROP DATABASE IF EXISTS \"$RUN_DB\" WITH (FORCE)" -c "CREATE DATABASE \"$RUN_DB\" TEMPLATE \"$TEMPLATE_DB\""
  psql_perf "$RUN_DB" -c 'SELECT pg_stat_statements_reset()' >/dev/null
  say "run database $RUN_DB recreated from the template"
}

cmd_api_start() { start_api "$RUN_DB"; }

# Starts the Release API against ONE of our databases. The cookie-encryption keys live in .results/dpkeys, so a session signed in earlier
# stays valid after the API restarts (and after "reset", because the template database already holds the test accounts).
start_api() {
  local database="$1"
  assert_perf_db "$database"
  load_secrets
  api_is_ours && { say "the API is already running (pid $(api_pid))"; return 0; }
  port_in_use "$API_PORT" && die "port $API_PORT is in use by something else"
  database_exists "$database" || die "database $database does not exist yet: run '$0 reset' (or 'fixtures' for the template)"
  mkdir -p "$RESULTS/dpkeys" && chmod 700 "$RESULTS/dpkeys"
  local out="$REPO/backend/controllers/bin/Release/net10.0"
  [ -f "$out/TechStore.Api.dll" ] || die "no Release build yet: run '$0 fixtures'"
  # env -i: the API sees ONLY what is listed here. The Testing environment is what allows the test accounts (it refuses them elsewhere).
  ( cd "$out"; env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
      ASPNETCORE_ENVIRONMENT=Testing ASPNETCORE_URLS="http://$API_HOST:$API_PORT" \
      ConnectionStrings__TechStore="Host=$PG_HOST;Port=$PG_PORT;Database=$database;Username=$APP_ROLE;Password=$PERF_APP_PASSWORD" \
      DataProtection__KeysPath="$RESULTS/dpkeys" \
      Auth__TestAccounts__0__Email="perf.admin@perf.test" Auth__TestAccounts__0__Password="$PERF_ACCOUNT_PASSWORD" Auth__TestAccounts__0__Role="Admin" \
      Auth__TestAccounts__1__Email="perf.sales@perf.test" Auth__TestAccounts__1__Password="$PERF_ACCOUNT_PASSWORD" Auth__TestAccounts__1__Role="Sales" \
      Auth__TestAccounts__2__Email="perf.warehouse@perf.test" Auth__TestAccounts__2__Password="$PERF_ACCOUNT_PASSWORD" Auth__TestAccounts__2__Role="Warehouse" \
      nohup dotnet TechStore.Api.dll > "$RESULTS/api.log" 2>&1 < /dev/null & echo $! > "$RESULTS/api.pid" )
  for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "http://$API_HOST:$API_PORT/health" || true)" = "200" ] && { say "the API is healthy on http://$API_HOST:$API_PORT"; return 0; }
    sleep 1
  done
  tail -n 20 "$RESULTS/api.log" >&2 || true
  die "the API did not become healthy"
}

api_stop_quiet() { cmd_api_stop >/dev/null; }

cmd_api_stop() {
  if api_is_ours; then
    local pid; pid="$(api_pid)"; kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
    say "the API was stopped"
  fi
  rm -f "$RESULTS/api.pid"
}

cmd_sessions() { # sessions 10:sales 2:warehouse 1:admin
  [ "$#" -ge 1 ] || die "usage: sessions COUNT:ROLE [COUNT:ROLE ...]   (roles: admin, sales, warehouse)"
  load_secrets
  api_is_ours || die "the API is not running: run '$0 api start'"
  PERF_ACCOUNT_PASSWORD="$PERF_ACCOUNT_PASSWORD" python3 -I "$HERE/sessions.py" --host "$API_HOST" --port "$API_PORT" \
    --out "$RESULTS/sessions.json" --pace "$PACE_SECONDS" "$@"
}

cmd_sessions_check() { # are the saved sessions still accepted by the running API?
  load_secrets
  api_is_ours || die "the API is not running: run '$0 api start'"
  [ -f "$RESULTS/sessions.json" ] || die "no sessions yet: run '$0 sessions ...'"
  python3 -I "$HERE/sessions.py" --host "$API_HOST" --port "$API_PORT" --out "$RESULTS/sessions.json" --pace 0 --check
}

# ---- Resource sampler: while a benchmark runs, records the API process, the k6 process and the database container every ~3 s ----------
SAMPLER_PID=""
start_sampler() { # $1 = CSV file
  local file="$1" apid; apid="$(api_pid)"
  echo "epoch,api_cpu_pct,api_rss_mb,k6_cpu_pct,k6_rss_mb,db_cpu_pct,db_mem_mb" > "$file"
  (
    set +e # one failed sample (for example no k6 process yet) must never stop the sampler
    while :; do
      api="$(ps -p "$apid" -o %cpu=,rss= 2>/dev/null | awk '{printf "%s,%.1f", $1, $2/1024}')"
      kpids="$(pgrep -x k6 | paste -sd, - || true)"
      k6="$([ -n "$kpids" ] && ps -p "$kpids" -o %cpu=,rss= 2>/dev/null | awk '{c+=$1; r+=$2} END {printf "%.1f,%.1f", c, r/1024}')"
      db="$(docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' "$CONTAINER" 2>/dev/null | awk '{gsub("%","",$1); m=$2; v=m+0; if (m ~ /GiB/) v=v*1024; printf "%s,%.1f", $1, v}')"
      echo "$(date +%s),${api:-0,0},${k6:-0,0},${db:-0,0}" >> "$file"
      sleep 1
    done
  ) &
  SAMPLER_PID=$!
}
stop_sampler() { [ -n "$SAMPLER_PID" ] && kill "$SAMPLER_PID" 2>/dev/null || true; wait "$SAMPLER_PID" 2>/dev/null || true; SAMPLER_PID=""; }

# k6 against the isolated API: $1 script, $2 summary file, the rest are extra "-e KEY=VALUE" pairs for the script.
run_k6() {
  local script="$1" summary="$2"; shift 2
  local first count
  first="$(psql_perf "$RUN_DB" -tAc "SELECT coalesce(min(order_id), 0) FROM orders WHERE status = 'Draft' AND order_id > 1000")"
  count="$(psql_perf "$RUN_DB" -tAc "SELECT count(*) FROM orders WHERE status = 'Draft' AND order_id > 1000")"
  env -i PATH="$PATH" HOME="$HOME" k6 run --quiet \
    -e BASE_URL="http://$API_HOST:$API_PORT" -e SESSIONS_FILE="$RESULTS/sessions.json" -e DRAFT_FIRST="$first" -e DRAFT_COUNT="$count" \
    "$@" --summary-export "$summary" "$HERE/k6/$script"
}

# bench reads|reservations CLIENTS ITERATIONS : warm-up + measured iterations at a fixed number of concurrent clients (Step 14C).
cmd_bench() {
  local kind="${1:-}" clients="${2:-}" iterations="${3:-}"
  [[ "$kind" =~ ^(reads|reservations)$ ]] && [[ "$clients" =~ ^[0-9]+$ ]] && [[ "$iterations" =~ ^[0-9]+$ ]] || die "usage: bench reads|reservations CLIENTS ITERATIONS"
  load_secrets
  command -v k6 >/dev/null || die "k6 is not installed (brew install k6)"
  [ -f "$RESULTS/sessions.json" ] || die "no sessions yet: run '$0 sessions $clients:sales'"
  local dir="$RESULTS/baseline"; mkdir -p "$dir"
  local warm measure i
  if [ "$kind" = reads ]; then warm=30; measure=60; else
    if [ "$clients" -le 1 ]; then warm=50; measure=300; else warm=200; measure=1000; fi
  fi
  for i in $(seq 1 "$iterations"); do
    local tag="$kind-c$clients-i$i"
    say "== $tag: $([ "$kind" = reads ] && echo "warm-up ${warm}s, measured ${measure}s" || echo "warm-up $warm requests, measured $measure requests")"
    if [ "$kind" = reservations ]; then
      # Every iteration starts from the same state: the run database is recreated from the template and gets fresh Draft orders.
      cmd_reset >/dev/null
      cmd_api_start >/dev/null
      cmd_drafts $((warm + measure)) 2 >/dev/null
    else
      api_is_ours || cmd_api_start >/dev/null
      [ "$i" -gt 1 ] && sleep 10 # a short rest between iterations
    fi
    cmd_sessions_check >/dev/null || die "the saved sessions are no longer accepted"
    psql_perf "$RUN_DB" -c 'SELECT pg_stat_statements_reset()' >/dev/null
    start_sampler "$dir/resources-$tag.csv"
    local status=0
    if [ "$kind" = reads ]; then
      run_k6 baseline-reads.js "$dir/$tag.json" -e VUS="$clients" -e WARMUP_S="$warm" -e MEASURE_S="$measure" > "$dir/$tag.log" 2>&1 || status=$?
    else
      run_k6 baseline-reservations.js "$dir/$tag.json" -e VUS="$clients" -e WARMUP_N="$warm" -e MEASURE_N="$measure" > "$dir/$tag.log" 2>&1 || status=$?
    fi
    stop_sampler
    psql_perf "$RUN_DB" -tAc "SELECT 'statements: ' || sum(calls) FROM pg_stat_statements WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())" > "$dir/dbcalls-$tag.txt" || true
    cmd_pgstats 12 > "$dir/pgstats-$tag.txt" 2>&1 || true
    cmd_invariants "$RUN_DB" > "$dir/invariants-$tag.txt" 2>&1 || { say "INVARIANT FAILURE in $tag (see $dir/invariants-$tag.txt)"; status=1; }
    [ "$status" -eq 0 ] || say "k6 exited with status $status for $tag (a failed k6 threshold or error: see $dir/$tag.log)"
  done
}

cmd_pgstats() { # pgstats [N] : the N statements that took the most database time since the last reset
  load_secrets
  psql_perf "$RUN_DB" -c "
    SELECT calls, round(total_exec_time::numeric, 1) AS total_ms, round(mean_exec_time::numeric, 3) AS mean_ms, rows,
           left(regexp_replace(query, '\s+', ' ', 'g'), 100) AS statement
    FROM pg_stat_statements WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
    ORDER BY total_exec_time DESC LIMIT ${1:-12}"
}

cmd_report() { python3 -I "$HERE/summarize.py" "$RESULTS/${1:-baseline}"; }

cmd_drafts() {
  load_secrets
  local count="${1:-}" lines="${2:-2}"
  [[ "$count" =~ ^[0-9]+$ ]] && [[ "$lines" =~ ^[1-3]$ ]] || die "usage: drafts COUNT [LINES 1-3]"
  psql_perf "$RUN_DB" -v draft_count="$count" -v draft_lines="$lines" -f "$HERE/sql/04-drafts.sql"
  say "added $count Draft orders ($lines line(s) each): order ids 1001 to $((1000 + count))"
}

cmd_invariants() { load_secrets; psql_perf "${1:-$RUN_DB}" -f "$HERE/sql/03-invariants.sql"; }

cmd_run() { # run SCRIPT.js [extra k6 arguments]
  load_secrets
  command -v k6 >/dev/null || die "k6 is not installed (brew install k6)"
  local script="${1:-}"; shift || true
  [ -f "$HERE/k6/$script" ] || die "no such script in tests/performance/k6: $script"
  api_is_ours || die "the API is not running: run '$0 api start'"
  [ -f "$RESULTS/sessions.json" ] || die "no sessions yet: run '$0 sessions ...'"
  local first count
  first="$(psql_perf "$RUN_DB" -tAc "SELECT coalesce(min(order_id), 0) FROM orders WHERE status = 'Draft' AND order_id > 1000")"
  count="$(psql_perf "$RUN_DB" -tAc "SELECT count(*) FROM orders WHERE status = 'Draft' AND order_id > 1000")"
  mkdir -p "$RESULTS/k6"
  local stamp; stamp="$(date +%Y%m%d-%H%M%S)"
  # env -i: k6 gets only what is listed (no cloud tokens, no proxies). The script itself refuses any other target.
  env -i PATH="$PATH" HOME="$HOME" k6 run \
    -e BASE_URL="http://$API_HOST:$API_PORT" -e SESSIONS_FILE="$RESULTS/sessions.json" \
    -e DRAFT_FIRST="$first" -e DRAFT_COUNT="$count" \
    --summary-export "$RESULTS/k6/${script%.js}-$stamp.json" "$@" "$HERE/k6/$script"
}

cmd_smoke() {
  command -v k6 >/dev/null || die "k6 is not installed (brew install k6)"
  cmd_reset
  cmd_api_start
  cmd_sessions 2:sales 1:warehouse
  cmd_drafts 5 2
  # Whatever happens in the k6 run, the invariants are still checked and the API is always stopped.
  local status=0
  cmd_run smoke.js || status=$?
  cmd_invariants || status=$?
  cmd_api_stop
  [ "$status" -eq 0 ] || die "smoke test FAILED (see above)"
  say "smoke test passed"
}

cmd_info() { # machine, runtime and configuration details to record next to any benchmark result
  load_secrets
  mkdir -p "$RESULTS"
  {
    echo "recorded:      $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "git commit:    $(git -C "$REPO" rev-parse --short HEAD) on $(git -C "$REPO" branch --show-current)"
    echo "os:            $(sw_vers -productName 2>/dev/null) $(sw_vers -productVersion 2>/dev/null) ($(uname -m))"
    echo "cpu:           $(sysctl -n machdep.cpu.brand_string 2>/dev/null) , $(sysctl -n hw.ncpu 2>/dev/null) logical cores"
    echo "memory:        $(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1073741824 )) GB"
    echo "power:         $(pmset -g batt 2>/dev/null | head -2 | tr '\n' ' ')"
    echo ".NET SDK:      $(dotnet --version)"
    echo "k6:            $(k6 version 2>/dev/null || echo 'not installed')"
    echo "docker:        $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo 'not running'); VM: $(docker info --format '{{.NCPU}} CPUs, {{.MemTotal}} bytes' 2>/dev/null || echo '-')"
    echo "postgres:      $(psql_perf postgres -tAc 'SHOW server_version' 2>/dev/null || echo 'not running')"
    echo "pg settings:   $(psql_perf postgres -tAc "SELECT string_agg(name || '=' || setting, ', ') FROM pg_settings WHERE name IN ('max_connections','shared_buffers','work_mem','effective_cache_size','synchronous_commit','fsync')" 2>/dev/null || echo '-')"
    echo "api:           Release build, ASPNETCORE_ENVIRONMENT=Testing, Npgsql default pool (100), connecting as limited role $APP_ROLE"
    echo "dataset:       $(cat "$RESULTS/dataset-checksum.txt" 2>/dev/null || echo 'no checksum yet')"
    echo "differences from production: Testing environment (plain http, test accounts); no nginx proxy; no container CPU/memory limits"
  } | tee "$RESULTS/environment.txt"
}

cmd_down() {
  [ -d "$RESULTS" ] || { say "nothing to clean up"; return 0; }
  cmd_api_stop
  if command -v docker >/dev/null && docker info >/dev/null 2>&1 && docker inspect "$CONTAINER" >/dev/null 2>&1; then
    [ "$(docker inspect -f '{{index .Config.Labels "techstore.perf"}}' "$CONTAINER")" = "1" ] || die "$CONTAINER is not ours: not removing it"
    docker rm -f -v "$CONTAINER" >/dev/null
    say "removed the performance database container and its volume"
  fi
  # Only the files this tool creates, by name: never a recursive delete.
  rm -f "$RESULTS/perf.env" "$RESULTS/api.pid" "$RESULTS/api.log" "$RESULTS/sessions.json" "$RESULTS/environment.txt" \
        "$RESULTS/dataset-checksum.txt" "$RESULTS/fixture-validation.txt" "$RESULTS/smoke.log" "$RESULTS"/k6/*.json "$RESULTS"/baseline/* "$RESULTS"/baseline-run1/* "$RESULTS"/dpkeys/*
  rmdir "$RESULTS/k6" "$RESULTS/baseline" "$RESULTS/baseline-run1" "$RESULTS/dpkeys" 2>/dev/null || true
  rmdir "$RESULTS" 2>/dev/null || say "left $RESULTS in place: it holds files this tool did not create"
  say "cleaned up"
}

cmd_guard_test() { # proves that the guards refuse wrong targets (needs no database)
  local failures=0
  expect_refusal() { if ( "$@" ) >/dev/null 2>&1; then echo "  NOT REFUSED: $*"; failures=$((failures + 1)); else echo "  refused:     $*"; fi; }
  expect_refusal assert_perf_db techstore
  expect_refusal assert_perf_db techstore_test
  expect_refusal assert_perf_db techstore_api_test
  expect_refusal assert_perf_db postgres
  expect_refusal assert_perf_db 'techstore_perf_x"; DROP DATABASE techstore; --'
  expect_refusal assert_perf_db techstore_perf_UPPER
  [ "$PG_PORT" != "5432" ] && [ "$API_PORT" != "5111" ] && [ "$API_PORT" != "8080" ] || failures=$((failures + 1))
  echo "  ports:       database $PG_PORT (not 5432), API $API_PORT (not 5111 or 8080)"
  [ "$failures" -eq 0 ] && say "guard test passed" || die "guard test FAILED ($failures)"
}

cmd_help() {
  cat <<HELP
tests/performance/perf.sh COMMAND

  up                 start the disposable PostgreSQL 17 container ($CONTAINER, $PG_HOST:$PG_PORT); starts Docker Desktop if needed
  fixtures           (re)build the template database: migrations + 10,000 products + 1,000 orders; validate; print the checksum
  reset              recreate the run database ($RUN_DB) from the template (use before every benchmark iteration)
  api start|stop     run the Release API (Testing environment) on http://$API_HOST:$API_PORT against the run database
  sessions N:ROLE... sign in N independent sessions per role (admin, sales, warehouse), paced under the login rate limit
  drafts N [LINES]   add N fresh Draft orders (ids 1001+) with their own stock rows, for reservation benchmarks
  run SCRIPT.js      run a k6 script from tests/performance/k6 against the isolated API
  invariants         check the stock and order invariants in the run database
  sessions-check     check that the saved sessions are still accepted by the running API
  bench KIND C N     baseline: KIND reads|reservations, C concurrent clients, N measured iterations (warm-up included)
  pgstats [N]        the N SQL statements with the most database time since the last reset
  report             summarize the baseline results (percentiles, throughput, error rates, resources)
  smoke              reset + API + sessions + drafts + k6 smoke test + invariants + stop
  info               record machine, runtime and configuration details
  down               remove the container, volume and every file this tool created
  guard-test         prove that the safety guards refuse wrong targets
HELP
}

case "${1:-help}" in
  up) cmd_up ;;
  fixtures) cmd_fixtures ;;
  reset) cmd_reset ;;
  api) case "${2:-}" in start) cmd_api_start ;; stop) cmd_api_stop ;; *) die "usage: api start|stop" ;; esac ;;
  sessions) shift; cmd_sessions "$@" ;;
  drafts) shift; cmd_drafts "$@" ;;
  run) shift; cmd_run "$@" ;;
  invariants) cmd_invariants ;;
  sessions-check) cmd_sessions_check ;;
  bench) shift; cmd_bench "$@" ;;
  pgstats) shift; cmd_pgstats "$@" ;;
  report) shift; cmd_report "$@" ;;
  smoke) cmd_smoke ;;
  info) cmd_info ;;
  down) cmd_down ;;
  guard-test) cmd_guard_test ;;
  help|-h|--help) cmd_help ;;
  *) cmd_help; exit 2 ;;
esac
