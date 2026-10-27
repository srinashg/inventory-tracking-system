#!/usr/bin/env bash
# End-to-end test of the real Docker Compose stack in a real browser (Step 11F).
# Starts compose.yaml from an EMPTY state under its own project name with its own random secrets (never your real stack or
# your development database), creates a Sales and a Warehouse test account, runs the Playwright tests in
# frontend/e2e-compose, removes and recreates every container (volumes kept), runs the second set of tests, checks the
# database and the logs, and deletes everything it created.
#   Usage (from the repository root):  bash tests/containers/compose-e2e.sh
set -u
cd "$(dirname "$0")/../.."

PROJECT=techstore-composee2e
PORT=58092
U=localhost:$PORT
WORK=$(mktemp -d); chmod 700 "$WORK"
ENVFILE=$WORK/test.env
umask 077
DBPW=$(openssl rand -hex 24); APPPW=$(openssl rand -hex 24)
LOGIN_PW="Pw-$(openssl rand -hex 14)"
# shellcheck source=tests/containers/ci-support.sh
. tests/containers/ci-support.sh
ci_mask "$DBPW" "$APPPW" "$LOGIN_PW"
ADMIN=admin@composee2e.example; SALES=sales@composee2e.example; WAREHOUSE=warehouse@composee2e.example
cat > "$ENVFILE" <<ENV
POSTGRES_PASSWORD=$DBPW
TECHSTORE_APP_DB_PASSWORD=$APPPW
TECHSTORE_ADMIN_EMAIL=$ADMIN
TECHSTORE_ADMIN_PASSWORD=$LOGIN_PW
TECHSTORE_WEB_PORT=$PORT
ENV

pass=0; fail=0
ok()   { pass=$((pass+1)); echo "  ok   $1"; }
bad()  { fail=$((fail+1)); echo "  FAIL $1"; [ -n "${2:-}" ] && echo "       $2"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3] got [$2]"; fi; }
has()  { if printf '%s' "$2" | grep -qi -- "$3"; then ok "$1"; else bad "$1" "[$3] not found in: $(printf '%s' "$2" | head -c 300)"; fi; }
hasnt(){ if printf '%s' "$2" | grep -qi -- "$3"; then bad "$1" "[$3] found"; else ok "$1"; fi; }

dc() { docker compose -p $PROJECT --env-file "$ENVFILE" "$@"; }
cleanup() {
  local rc=$?
  if [ -n "${CI_LOG_DIR:-}" ] && { [ "$fail" -gt 0 ] || [ "$rc" -ne 0 ]; }; then
    dc logs --no-color 2>&1 | ci_save_logs compose-e2e "$DBPW" "$APPPW" "$LOGIN_PW"
  fi
  dc down -v >/dev/null 2>&1; rm -rf "$WORK"
}
trap cleanup EXIT
dc down -v >/dev/null 2>&1
psql_db() { dc exec -T db psql -U techstore -d techstore -tA -c "$1" | tr -d '\r'; }
wait_healthy() { for _ in $(seq 60); do curl -sf $U/health >/dev/null && [ "$(dc ps -a --format '{{.Service}}={{.Health}}' | grep -c 'healthy')" -ge 3 ] && return 0; sleep 2; done; return 1; }
snapshot() { psql_db "select (select count(*) from products)||'/'||(select count(*) from orders)||'/'||(select count(*) from order_items)||'/'||(select count(*) from inventory_movements)||'/'||(select count(*) from inventory_balances)||'/'||(select count(*) from users)||'/'||(select count(*) from user_roles)||'/'||(select count(*) from roles)||'/'||(select count(*) from \"__EFMigrationsHistory\")"; }
playwright() { (cd frontend && COMPOSE_BASE_URL=http://$U COMPOSE_PASSWORD="$LOGIN_PW" COMPOSE_STATE_DIR="$WORK" COMPOSE_ADMIN_EMAIL=$ADMIN COMPOSE_SALES_EMAIL=$SALES COMPOSE_WAREHOUSE_EMAIL=$WAREHOUSE npx playwright test -c playwright.compose.config.ts "$@" 2>&1); }

echo "== 1. clean first-time start: PostgreSQL, migrations, limited user, first Admin"
check "no volumes exist beforehand" "$(docker volume ls -q | grep -c "^${PROJECT}_")" 0
dc up --build -d >/dev/null 2>&1 || { echo "compose up failed"; dc logs --tail 40; exit 1; }
wait_healthy || bad "all services healthy" "$(dc ps -a)"
states=$(dc ps -a --format '{{.Service}}={{.State}}/{{.Health}}' | sort | tr '\n' ' ')
has "db healthy"  "$states" 'db=running/healthy'
has "api healthy" "$states" 'api=running/healthy'
has "web healthy" "$states" 'web=running/healthy'
check "migrate ran to completion" "$(docker inspect -f '{{.State.ExitCode}}/{{.State.Status}}' $(dc ps -a -q migrate))" "0/exited"
check "all migrations recorded" "$(psql_db 'select count(*) from "__EFMigrationsHistory"')" 2
check "the limited role exists and is not a superuser" "$(psql_db "select rolsuper::int::text||rolcreatedb::int||rolcreaterole::int from pg_roles where rolname='techstore_app'")" 000
check "exactly one Admin, created from the settings" "$(psql_db "select count(*) from users u join user_roles ur on ur.user_id=u.id join roles r on r.id=ur.role_id where r.name='Admin' and u.email='$ADMIN'")" 1
check "the web server reaches the API (health through nginx)" "$(curl -s $U/health)" Healthy
has   "the React app is served" "$(curl -s $U/login)" 'id="root"'

echo "== test accounts and reference data (the app has no screens for these yet)"
# Sales and Warehouse accounts reuse the Admin's password HASH (made by ASP.NET Core Identity at first start), so no hashing is done here.
for pair in "$SALES:Sales:Sales Tester" "$WAREHOUSE:Warehouse:Warehouse Tester"; do
  email=${pair%%:*}; rest=${pair#*:}; role=${rest%%:*}; name=${rest#*:}
  psql_db "insert into users (display_name,user_name,normalized_user_name,email,normalized_email,email_confirmed,password_hash,security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count)
           select '$name','$email',upper('$email'),'$email',upper('$email'),true,password_hash,upper(md5(random()::text)),gen_random_uuid()::text,false,false,true,0 from users where email='$ADMIN'" >/dev/null
  psql_db "insert into user_roles select u.id, r.id from users u, roles r where u.email='$email' and r.name='$role'" >/dev/null
done
psql_db "insert into warehouses (name, location) values ('Dallas','Dallas, TX'),('Seattle','Seattle, WA')" >/dev/null
psql_db "insert into customers (name, email) values ('Amelia Reed','amelia@composee2e.example'),('Marcus Bell','marcus@composee2e.example')" >/dev/null
check "three accounts: one per role" "$(psql_db "select string_agg(r.name, ',' order by r.name) from user_roles ur join roles r on r.id=ur.role_id")" "Admin,Sales,Warehouse"

echo "== 2-5. real browser, phase 1 (fresh stack): roles, workflow, insufficient stock"
out=$(playwright e2e-compose/01-before-restart.spec.ts); echo "$out" | sed 's/^/    /'
has "phase 1 browser tests passed" "$out" ' 7 passed'
hasnt "...with no failure" "$out" 'failed'
before=$(snapshot); keys_before=$(dc exec -T api sh -c 'ls /keys | sort' | tr -d '\r')
check "the browser workflow left exactly 8 laptops on hand" "$(psql_db "select on_hand||'/'||reserved from inventory_balances ib join products p on p.product_id=ib.product_id where p.sku like 'FLOW-%'")" "8/0"
check "the refused order of 15 stayed a draft with no movement" "$(psql_db "select status from orders where order_id=2")" "Draft"
check "one key file exists" "$(echo "$keys_before" | grep -c 'key-')" 1

echo "== 6-7. remove and recreate every container (volumes kept)"
before_images=$(docker inspect -f '{{.Id}}' $(dc ps -q) | sort | tr '\n' ' ')
dc down >/dev/null 2>&1
check "the containers are gone but the volumes remain" "$(docker volume ls -q | grep -c "^${PROJECT}_")" 2
dc up -d >/dev/null 2>&1; wait_healthy || bad "healthy after recreate" "$(dc ps -a)"
check "these are new containers" "$([ "$before_images" != "$(docker inspect -f '{{.Id}}' $(dc ps -q) | sort | tr '\n' ' ')" ] && echo yes)" yes
check "migrate ran again and exited 0" "$(docker inspect -f '{{.State.ExitCode}}' $(dc ps -a -q migrate))" 0
check "every record and migration count is unchanged" "$(snapshot)" "$before"
check "the Data Protection key ring is the same one" "$(dc exec -T api sh -c 'ls /keys | sort' | tr -d '\r')" "$keys_before"
check "still exactly three accounts" "$(psql_db 'select count(*) from users')" 3
echo "== real browser, phase 2: the old sessions still work, the data is still there"
out=$(playwright e2e-compose/02-after-restart.spec.ts); echo "$out" | sed 's/^/    /'
has "phase 2 browser tests passed" "$out" ' 3 passed'
hasnt "...with no failure" "$out" 'failed'

echo "== 7. running migration and provisioning by hand, twice more"
dc run --rm migrate >/dev/null 2>&1; check "first manual run exits 0" "$?" 0
dc run --rm migrate >/dev/null 2>&1; check "second manual run exits 0" "$?" 0
check "nothing changed" "$(snapshot)" "$before"

echo "== 8. the API still runs as the limited user"
check "the role is still limited" "$(psql_db "select rolsuper::int::text||rolcreatedb::int||rolcreaterole::int||rolreplication::int||rolbypassrls::int from pg_roles where rolname='techstore_app'")" 00000
check "the API holds connections as techstore_app" "$([ "$(psql_db "select count(*) from pg_stat_activity where usename='techstore_app'")" -ge 1 ] && echo yes)" yes
check "the API process is configured with techstore_app, not the owner" "$(dc exec -T api sh -c 'tr "\0" "\n" < /proc/1/environ' | grep ConnectionStrings | sed 's/.*Username=\([^;]*\);.*/\1/' | tr -d '\r')" techstore_app
check "it holds no table privileges beyond select/insert/update (+delete on user_roles)" "$(psql_db "select string_agg(distinct privilege_type, ',' order by privilege_type) from information_schema.role_table_grants where grantee='techstore_app' and table_name<>'user_roles'")" "INSERT,SELECT,UPDATE"
check "...and no DELETE on business tables" "$(psql_db "select count(*) from information_schema.role_table_grants where grantee='techstore_app' and privilege_type='DELETE' and table_name<>'user_roles'")" 0
check "...and no write access to the migration history" "$(psql_db "select count(*) from information_schema.role_table_grants where grantee='techstore_app' and table_name='__EFMigrationsHistory' and privilege_type<>'SELECT'")" 0

echo "== 9. no secrets in logs or images"
logs=$(dc logs 2>&1)
for secret in "$DBPW" "$APPPW" "$LOGIN_PW"; do hasnt "a secret is absent from every container log" "$logs" "$secret"; done
for img in $(dc images -q | sort -u); do
  text=$(docker inspect "$img" --format '{{json .Config.Env}}'; docker history --no-trunc "$img")
  for secret in "$DBPW" "$APPPW" "$LOGIN_PW"; do hasnt "image $(echo $img | head -c 12): a secret is absent" "$text" "$secret"; done
done
hasnt "no secret in the files that ship in the repository" "$(grep -rlE "$DBPW|$APPPW|$LOGIN_PW" compose.yaml .env.example backend frontend/nginx frontend/Dockerfile frontend/src frontend/e2e-compose tests/containers 2>/dev/null)" '.'

echo
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
