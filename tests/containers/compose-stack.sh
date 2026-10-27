#!/usr/bin/env bash
# Container test for the whole Compose stack (Step 11D). It starts compose.yaml under its OWN project name and with its OWN
# random secrets, so it never touches your real stack, its volumes, or your development database. Everything it creates is
# removed at the end ("down -v" on that throw-away project only).
#   Usage (from the repository root):  bash tests/containers/compose-stack.sh
set -u
cd "$(dirname "$0")/../.."

PROJECT=techstore-composetest
PORT=58090
U=localhost:$PORT
WORK=$(mktemp -d)
ENVFILE=$WORK/test.env
JAR=$WORK/cookies.txt
umask 077
DBPW=$(openssl rand -hex 24)
APPPW=$(openssl rand -hex 24)
ADMIN_EMAIL=admin@composetest.example
ADMIN_PW="Adm-$(openssl rand -hex 12)"
# shellcheck source=tests/containers/ci-support.sh
. tests/containers/ci-support.sh
ci_mask "$DBPW" "$APPPW" "$ADMIN_PW"
cat > "$ENVFILE" <<ENV
POSTGRES_PASSWORD=$DBPW
TECHSTORE_APP_DB_PASSWORD=$APPPW
TECHSTORE_ADMIN_EMAIL=$ADMIN_EMAIL
TECHSTORE_ADMIN_PASSWORD=$ADMIN_PW
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
    dc logs --no-color 2>&1 | ci_save_logs compose-stack "$DBPW" "$APPPW" "$ADMIN_PW"
  fi
  dc down -v >/dev/null 2>&1; rm -rf "$WORK"
}
trap cleanup EXIT
dc down -v >/dev/null 2>&1

csrf()  { curl -s -b "$JAR" -c "$JAR" $U/api/auth/csrf | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])'; }
login() { # login <email> <password> [extra curl args]  -> prints the HTTP status
  local t; t=$(csrf)
  curl -s -b "$JAR" -c "$JAR" -H "X-CSRF-TOKEN: $t" -H 'Content-Type: application/json' "${@:3}" \
       -d "{\"email\":\"$1\",\"password\":\"$2\"}" -o /dev/null -w '%{http_code}' $U/api/auth/login
}
status() { curl -s -b "$JAR" -o /dev/null -w '%{http_code}' "$U$1"; }
wait_healthy() { for _ in $(seq 60); do curl -sf $U/health >/dev/null && return 0; sleep 2; done; return 1; }
psql_db() { dc exec -T db psql -U techstore -d techstore -tA -c "$1" | tr -d '\r'; }

echo "== a missing password is refused with a clear message"
msg=$(POSTGRES_PASSWORD= dc --env-file /dev/null config 2>&1 || true)
has "compose names the missing setting" "$msg" 'POSTGRES_PASSWORD'
msg=$(POSTGRES_PASSWORD=x dc --env-file /dev/null config 2>&1 || true)
has "...and the missing application-user password too" "$msg" 'TECHSTORE_APP_DB_PASSWORD'

echo "== first start: database, migration, API, web"
dc up --build -d >/dev/null 2>&1 || { echo "compose up failed"; dc logs --tail 40; exit 1; }
wait_healthy || bad "the stack became healthy" "$(dc ps -a)"
for _ in $(seq 30); do [ "$(dc ps -a --format '{{.Service}}={{.Health}}' | grep -c '^web=healthy')" = 1 ] && break; sleep 2; done
states=$(dc ps -a --format '{{.Service}}={{.State}}/{{.Health}}' | sort | tr '\n' ' ')
has "db is healthy"  "$states" 'db=running/healthy'
has "api is healthy" "$states" 'api=running/healthy'
has "web is healthy" "$states" 'web=running/healthy'
check "the migration container ran to completion (exit 0)" "$(docker inspect -f '{{.State.ExitCode}} {{.State.Status}}' $(dc ps -a -q migrate))" "0 exited"
check "the API answers through the web server" "$(curl -s $U/health)" Healthy
check "the React app is served" "$(curl -s -o /dev/null -w '%{http_code}' $U/orders)" 200
check "only 127.0.0.1 publishes a port" "$(docker port $(dc ps -q web) | sort -u | tr '\n' ' ')" "8080/tcp -> 127.0.0.1:$PORT "
check "the database publishes no port" "$(docker port $(dc ps -q db) | wc -l | tr -d ' ')" 0
check "the API runs as a non-root user" "$(docker exec $(dc ps -q api) id -u)" 1654
check "the web server runs as a non-root user" "$(docker exec $(dc ps -q web) id -u)" 101

echo "== the schema exists and the migration is recorded"
tables=$(psql_db "select count(*) from information_schema.tables where table_schema='public'")
[ "$tables" -ge 10 ] && ok "tables created ($tables)" || bad "tables created" "$tables"
history=$(psql_db 'select count(*) from "__EFMigrationsHistory"')
check "every migration in the image is recorded" "$history" "$(docker run --rm --entrypoint sh $(dc images -q migrate | head -1) -c 'grep -c "INSERT INTO \"__EFMigrationsHistory\"" /migrate/migrate.sql')"
check "exactly one Admin was created from the settings" "$(psql_db "select count(*) from users where email='$ADMIN_EMAIL'")" 1

echo "== database initialization and security review checks (Step 11E)"
script=$(docker run --rm --entrypoint cat $(dc images -q migrate | head -1) /migrate/migrate.sql)
# A migration that drops or deletes data would run automatically on every "up"; this fails the moment one is added, so it gets a human review.
check "the migration script has no DROP, TRUNCATE or DELETE FROM statement" "$(printf '%s' "$script" | grep -ciE '^\s*(drop |truncate |delete from )|;\s*(drop |truncate |delete from )')" 0
check "every table is created only if its migration is not recorded yet (idempotent guard)" "$([ "$(printf '%s' "$script" | grep -c 'IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory"')" -ge 1 ] && echo yes)" yes
nopw=$(docker run --rm --network ${PROJECT}_backend -e PGCONNECT_TIMEOUT=5 postgres:17-alpine psql -w -h db -U techstore -d techstore -c 'select 1' 2>&1 || true)
has   "the database refuses a login without a password" "$nopw" 'no password supplied'
badpw=$(docker run --rm --network ${PROJECT}_backend -e PGCONNECT_TIMEOUT=5 -e PGPASSWORD=wrong postgres:17-alpine psql -h db -U techstore -d techstore -c 'select 1' 2>&1 || true)
has   "...and with a wrong password" "$badpw" 'password authentication failed'
check "the key ring file is readable only by the API user (mode 600)" "$(dc exec -T api sh -c 'stat -c %a /keys/key-*.xml' | tr -d '\r')" 600
has   "the API warns that local-only http cookies are on" "$(dc logs api 2>&1)" 'Local-only http cookie mode is on'

echo "== the API connects as the limited database user (Step 11E, option A)"
check "techstore_app is not a superuser, cannot create databases or roles, replicate or bypass row security" "$(psql_db "select (rolsuper::int)::text||rolcreatedb::int||rolcreaterole::int||rolreplication::int||rolbypassrls::int from pg_roles where rolname='techstore_app'")" 00000
appsql() { docker run --rm --network ${PROJECT}_backend -e PGCONNECT_TIMEOUT=5 -e PGPASSWORD="$APPPW" postgres:17-alpine psql -h db -U techstore_app -d techstore -tA -c "$1" 2>&1; }
for forbidden in "create table sneaky (id int)" "drop table products" "alter table products add column sneaky int" "truncate products" "create role sneaky" "create database sneaky" "copy (select 1) to program 'id'" "delete from products" "delete from \"__EFMigrationsHistory\""; do
  has "techstore_app is refused: $forbidden" "$(appsql "$forbidden")" 'permission denied\|must be'
done
check "techstore_app can read" "$(appsql 'select count(*) from products')" 0
check "the owner user keeps schema rights (it ran the migrations)" "$(psql_db "select has_schema_privilege('techstore','public','CREATE')")" t
check "the API really uses techstore_app (it has open connections)" "$([ "$(psql_db "select count(*) from pg_stat_activity where usename='techstore_app'")" -ge 1 ] && echo yes)" yes
hasnt "the API's connection string does not use the owner" "$(dc exec -T api sh -c 'tr "\0" "\n" < /proc/1/environ | grep ConnectionStrings' | sed 's/Password=[^;]*/Password=***/')" 'Username=techstore;'

echo "== signing in through the web server"
check "anonymous request is refused" "$(status /api/products)" 401
check "login succeeds" "$(login $ADMIN_EMAIL "$ADMIN_PW")" 200
check "the session works" "$(status /api/auth/me)" 200
cookie_line=$(grep techstore.session "$JAR")
has   "the session cookie is HttpOnly" "$cookie_line" '^#HttpOnly_'
check "the wrong password is refused" "$(login $ADMIN_EMAIL wrong-password-123)" 401
hdr=$(curl -s -D - -o /dev/null -b "$JAR" -c "$WORK/j2" -H "X-CSRF-TOKEN: $(csrf)" -H 'Content-Type: application/json' -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PW\"}" $U/api/auth/login)
has   "Set-Cookie has SameSite=Strict" "$hdr" 'set-cookie: techstore.session=.*samesite=strict'
has   "Set-Cookie has HttpOnly" "$hdr" 'set-cookie: techstore.session=.*httponly'
hasnt "local http: no Secure flag (local-only setting)" "$(printf '%s' "$hdr" | grep -i 'set-cookie: techstore.session')" '; secure'
hasnt "login response is not cacheable" "$hdr" 'cache-control: public'
check "a change without the CSRF token is refused" "$(curl -s -b "$JAR" -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d '{"sku":"C-1","name":"x","price":1}' $U/api/products)" 400
tok=$(csrf)
check "a change with the CSRF token works (Admin creates a product)" "$(curl -s -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' -H "X-CSRF-TOKEN: $tok" -H 'Content-Type: application/json' -d '{"sku":"COMPOSE-1","name":"Persistence probe","price":19.99}' $U/api/products)" 201

echo "== the full inventory workflow works through the limited database user"
WH=$(psql_db "insert into warehouses (name, location) values ('Dallas', 'Dallas, TX') returning warehouse_id" | head -1)
CU=$(psql_db "insert into customers (name, email) values ('Amelia Reed', 'amelia@composetest.example') returning customer_id" | head -1)
jpost() { curl -s -b "$JAR" -c "$JAR" -H "X-CSRF-TOKEN: $(csrf)" -H 'Content-Type: application/json' -d "${2:-{\}}" "$U$1"; }
jid() { python3 -c "import sys,json;print(json.load(sys.stdin)['$1'])"; }
PID=$(jpost /api/products '{"sku":"FLOW-1","name":"Laptop Pro 14","price":1299.99}' | jid productId)
jpost /api/inventory/receipts "{\"productId\":$PID,\"warehouseId\":$WH,\"quantity\":10}" >/dev/null
OID=$(jpost /api/orders "{\"customerId\":$CU,\"warehouseId\":$WH,\"items\":[{\"productId\":$PID,\"quantity\":2}]}" | jid orderId)
stock() { curl -s -b "$JAR" "$U/api/inventory?warehouseId=$WH" | python3 -c "import sys,json;i=[x for x in json.load(sys.stdin)['items'] if x['productId']==$PID][0];print(i['onHand'],i['reserved'],i['available'])"; }
check "received 10" "$(stock)" "10 0 10"
check "submit reserves 2" "$(jpost /api/orders/$OID/submit | jid status) $(stock)" "Reserved 10 2 8"
for step in pick pack ship; do jpost /api/orders/$OID/$step >/dev/null; done
check "pick, pack, ship leaves 8 available" "$(stock)" "8 0 8"
OID2=$(jpost /api/orders "{\"customerId\":$CU,\"warehouseId\":$WH,\"items\":[{\"productId\":$PID,\"quantity\":15}]}" | jid orderId)
check "ordering 15 of 8 available is refused (409)" "$(curl -s -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' -H "X-CSRF-TOKEN: $(csrf)" -X POST $U/api/orders/$OID2/submit)" 409
check "...and stock did not change" "$(stock)" "8 0 8"

echo "== restarting containers keeps the data and the login"
dc restart >/dev/null 2>&1; wait_healthy || bad "healthy after restart"
check "the session cookie still works after restarting every container" "$(status /api/auth/me)" 200
has   "the product is still there" "$(curl -s -b "$JAR" $U/api/products)" 'COMPOSE-1'

echo "== stopping and starting the stack (down, up) keeps the data and the login"
dc down >/dev/null 2>&1
dc up -d >/dev/null 2>&1; wait_healthy || bad "healthy after down/up"
check "the migration ran again and changed nothing (exit 0)" "$(docker inspect -f '{{.State.ExitCode}}' $(dc ps -a -q migrate))" 0
check "still exactly one Admin" "$(psql_db "select count(*) from users where email='$ADMIN_EMAIL'")" 1
check "the product survived down/up" "$(psql_db "select count(*) from products where sku='COMPOSE-1'")" 1
check "the old session cookie still works (key ring is on a volume)" "$(status /api/auth/me)" 200
has   "leftover Admin settings produce a warning, without values" "$(dc logs api 2>&1)" 'configured but not needed'

echo "== running the migration by hand is idempotent"
dc run --rm migrate >/dev/null 2>&1; check "migrate exits 0 again" "$?" 0
check "the product is untouched" "$(psql_db "select count(*) from products where sku='COMPOSE-1'")" 1

echo "== an older stack without the limited role gets it on the next start, and keeps every record"
before=$(psql_db "select (select count(*) from products)||'/'||(select count(*) from orders)||'/'||(select count(*) from users)")
psql_db "drop owned by techstore_app" >/dev/null; psql_db "drop role techstore_app" >/dev/null
check "(simulated old stack) the role is gone" "$(psql_db "select count(*) from pg_roles where rolname='techstore_app'")" 0
dc run --rm migrate >/dev/null 2>&1; check "migrate recreates it (exit 0)" "$?" 0
check "the role exists again" "$(psql_db "select count(*) from pg_roles where rolname='techstore_app'")" 1
check "no record was lost" "$(psql_db "select (select count(*) from products)||'/'||(select count(*) from orders)||'/'||(select count(*) from users)")" "$before"
dc restart api >/dev/null 2>&1; wait_healthy || bad "API healthy again with the recreated role"
check "the API works with the recreated role" "$(login $ADMIN_EMAIL "$ADMIN_PW")" 200

echo "== after the first start the Admin settings can be removed"
sed -i.bak 's/^TECHSTORE_ADMIN_PASSWORD=.*/TECHSTORE_ADMIN_PASSWORD=/; s/^TECHSTORE_ADMIN_EMAIL=.*/TECHSTORE_ADMIN_EMAIL=/' "$ENVFILE"; rm -f "$ENVFILE.bak"
dc up -d >/dev/null 2>&1; wait_healthy || bad "healthy without the Admin settings" "$(dc logs api 2>&1 | tail -5)"
check "the stack runs without them" "$(curl -s $U/health)" Healthy
check "the Admin can still sign in" "$(login $ADMIN_EMAIL "$ADMIN_PW")" 200
hasnt "no warning any more" "$(dc logs --since 20s api 2>&1)" 'configured but not needed'

echo "== one trusted proxy: forged X-Forwarded-For cannot dodge the login limit (10 per minute per client)"
jar_saved=$JAR; JAR=$WORK/limit.txt
codes=""
for i in $(seq 11); do codes="$codes $(login nobody@composetest.example "wrong-password-$i" -H "X-Forwarded-For: 10.9.8.$i")"; done
# (The sign-ins made earlier in this test already count toward the same minute, so the limit may bite a little before the 11th.)
has   "a 429 appears within 11 attempts although every one claimed a new address" "$codes" '429'
check "...and before that only ordinary 401 answers" "$(echo $codes | sed 's/ *429.*//' | tr ' ' '\n' | grep -vc '^401$')" 0
JAR=$jar_saved

echo "== a brand-new stack with no Admin settings refuses to start, with a clear message"
NOADMIN=$PROJECT-noadmin
cat > "$WORK/noadmin.env" <<ENV
POSTGRES_PASSWORD=$(openssl rand -hex 24)
TECHSTORE_APP_DB_PASSWORD=$(openssl rand -hex 24)
TECHSTORE_WEB_PORT=58091
TECHSTORE_SUBNET=172.29.0.0/24
TECHSTORE_WEB_IP=172.29.0.10
ENV
docker compose -p $NOADMIN --env-file "$WORK/noadmin.env" up --build -d >/dev/null 2>&1
for _ in $(seq 20); do [ "$(docker compose -p $NOADMIN --env-file "$WORK/noadmin.env" ps -a --format '{{.Service}}={{.State}}' | grep -c '^api=exited')" = 1 ] && break; sleep 3; done
msg=$(docker compose -p $NOADMIN --env-file "$WORK/noadmin.env" logs api 2>&1)
has   "the API stops and names the settings to provide" "$msg" 'Auth:InitialAdmin'
hasnt "...without printing any password" "$msg" 'password=\|Password='
check "nothing is left listening" "$(curl -s -o /dev/null -w '%{http_code}' localhost:58091/health)" 000
docker compose -p $NOADMIN --env-file "$WORK/noadmin.env" down -v >/dev/null 2>&1

echo "== no secrets leak"
logs=$(dc logs 2>&1)
hasnt "the database password is not in any log" "$logs" "$DBPW"
hasnt "the application-user password is not in any log" "$logs" "$APPPW"
hasnt "the admin password is not in any log" "$logs" "$ADMIN_PW"
for img in $(dc images -q | sort -u); do
  inspect=$(docker inspect "$img" --format '{{json .Config.Env}} {{.Comment}}'; docker history --no-trunc "$img")
  hasnt "image $(echo $img | head -c 12): no database password" "$inspect" "$DBPW"
  hasnt "image $(echo $img | head -c 12): no admin password" "$inspect" "$ADMIN_PW"
  hasnt "image $(echo $img | head -c 12): no application-user password" "$inspect" "$APPPW"
done
hasnt "the repository files contain no test secret" "$(grep -rl "$DBPW" compose.yaml .env.example backend frontend/nginx frontend/Dockerfile 2>/dev/null)" '.'

echo
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
