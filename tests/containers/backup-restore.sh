#!/usr/bin/env bash
# Tests the backup and restore procedure documented in README.md (Step 11H), using throw-away Compose stacks only.
# Stack A is filled with records and backed up; its data is then damaged and restored in place; finally the same backup is
# restored into a second, brand-new stack B (a different computer, in effect). A damaged backup file is also tried.
# It uses its own project names, ports and random secrets, never your .env, your running stack or your development database.
#   Usage (from the repository root):  bash tests/containers/backup-restore.sh
set -u
cd "$(dirname "$0")/../.."

A=techstore-restore-a; B=techstore-restore-b
PORT_A=58094; PORT_B=58095
WORK=$(mktemp -d); chmod 700 "$WORK"; umask 077
pass=0; fail=0
ok()   { pass=$((pass+1)); echo "  ok   $1"; }
bad()  { fail=$((fail+1)); echo "  FAIL $1"; [ -n "${2:-}" ] && echo "       $2"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3] got [$2]"; fi; }
differs(){ if [ "$2" != "$3" ]; then ok "$1"; else bad "$1" "both were [$2]"; fi; }

new_env() { # new_env <file> <port> [subnet-prefix]
  local prefix=${3:-172.28.0}
  cat > "$1" <<ENV
POSTGRES_PASSWORD=$(openssl rand -hex 24)
TECHSTORE_APP_DB_PASSWORD=$(openssl rand -hex 24)
TECHSTORE_ADMIN_EMAIL=admin@$(basename "$1" .env).example
TECHSTORE_ADMIN_PASSWORD=Pw-$(openssl rand -hex 14)
TECHSTORE_WEB_PORT=$2
TECHSTORE_SUBNET=$prefix.0/24
TECHSTORE_WEB_IP=$prefix.10
ENV
}
new_env "$WORK/stacka.env" $PORT_A 172.28.0
new_env "$WORK/stackb.env" $PORT_B 172.29.0
da() { docker compose -p $A --env-file "$WORK/stacka.env" "$@"; }
db_() { docker compose -p $B --env-file "$WORK/stackb.env" "$@"; }
cleanup() { da down -v >/dev/null 2>&1; db_ down -v >/dev/null 2>&1; rm -rf "$WORK"; }
trap cleanup EXIT
da down -v >/dev/null 2>&1; db_ down -v >/dev/null 2>&1

val() { grep "^$2=" "$WORK/$1.env" | cut -d= -f2-; }
wait_ready() { # wait_ready <port>
  for _ in $(seq 60); do curl -sf localhost:$1/health >/dev/null && return 0; sleep 2; done; return 1; }
psql_a() { da exec -T db psql -U techstore -d techstore -tA -c "$1" | tr -d '\r'; }
psql_b() { db_ exec -T db psql -U techstore -d techstore -tA -c "$1" | tr -d '\r'; }
# A fingerprint of everything business-relevant: a hash per table plus the accounts (email, role, password hash).
fingerprint() { # fingerprint <psql function>
  $1 "select md5(string_agg(h, '|' order by n)) from (
        select 'products' n, md5(coalesce(string_agg(x::text, ',' order by x::text), '')) h from products x union all
        select 'orders', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from orders x union all
        select 'order_items', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from order_items x union all
        select 'inventory_balances', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from inventory_balances x union all
        select 'inventory_movements', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from inventory_movements x union all
        select 'warehouses', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from warehouses x union all
        select 'customers', md5(coalesce(string_agg(x::text, ',' order by x::text), '')) from customers x union all
        select 'accounts', md5(coalesce(string_agg(u.email||':'||r.name||':'||u.password_hash, ',' order by u.email), ''))
          from users u join user_roles ur on ur.user_id = u.id join roles r on r.id = ur.role_id) t"
}
counts() { $1 "select (select count(*) from products)||'/'||(select count(*) from orders)||'/'||(select count(*) from inventory_movements)||'/'||(select count(*) from customers)||'/'||(select count(*) from users)"; }

# ---- helpers that talk to a running stack through its web port, like a browser would -----------------------------------
api_login() { # api_login <port> <jar> <email> <password>
  local t; t=$(curl -s -c "$2" localhost:$1/api/auth/csrf | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
  curl -s -b "$2" -c "$2" -H "X-CSRF-TOKEN: $t" -H 'Content-Type: application/json' -d "{\"email\":\"$3\",\"password\":\"$4\"}" -o /dev/null -w '%{http_code}' localhost:$1/api/auth/login; }
api_post() { # api_post <port> <jar> <path> [json]
  local t; t=$(curl -s -b "$2" -c "$2" localhost:$1/api/auth/csrf | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
  curl -s -b "$2" -c "$2" -H "X-CSRF-TOKEN: $t" -H 'Content-Type: application/json' -d "${4:-{\}}" localhost:$1$3; }
jid() { python3 -c "import sys,json;print(json.load(sys.stdin)['$1'])"; }

echo "== stack A: start it and fill it with records"
da up --build -d >/dev/null 2>&1 || { echo "compose up failed for A"; exit 1; }
wait_ready $PORT_A || bad "stack A healthy"
AE=$(val stacka TECHSTORE_ADMIN_EMAIL); AP=$(val stacka TECHSTORE_ADMIN_PASSWORD)
check "admin of A signs in" "$(api_login $PORT_A $WORK/a.jar $AE $AP)" 200
WH=$(psql_a "insert into warehouses (name, location) values ('Dallas','Dallas, TX') returning warehouse_id" | head -1)
CU=$(psql_a "insert into customers (name, email) values ('Amelia Reed','amelia@restore.example') returning customer_id" | head -1)
PID=$(api_post $PORT_A $WORK/a.jar /api/products '{"sku":"BACKUP-1","name":"Laptop Pro 14","price":1299.99}' | jid productId)
api_post $PORT_A $WORK/a.jar /api/inventory/receipts "{\"productId\":$PID,\"warehouseId\":$WH,\"quantity\":10}" >/dev/null
O1=$(api_post $PORT_A $WORK/a.jar /api/orders "{\"customerId\":$CU,\"warehouseId\":$WH,\"items\":[{\"productId\":$PID,\"quantity\":2}]}" | jid orderId)
for step in submit pick pack ship; do api_post $PORT_A $WORK/a.jar /api/orders/$O1/$step >/dev/null; done
O2=$(api_post $PORT_A $WORK/a.jar /api/orders "{\"customerId\":$CU,\"warehouseId\":$WH,\"items\":[{\"productId\":$PID,\"quantity\":1}]}" | jid orderId)
check "A holds 8 on hand, an order shipped, a draft waiting" "$(psql_a "select ib.on_hand||'/'||(select status from orders where order_id=$O1)||'/'||(select status from orders where order_id=$O2) from inventory_balances ib")" "8/Shipped/Draft"
FP_A=$(fingerprint psql_a); COUNTS_A=$(counts psql_a)
echo "    records in A (products/orders/movements/customers/users): $COUNTS_A"

echo "== backup (the exact commands from README.md; -T only because this script has no terminal)"
da exec -T db pg_dump -U techstore -Fc -f /tmp/techstore.dump techstore
da cp db:/tmp/techstore.dump "$WORK/techstore-backup.dump"
da exec -T db rm /tmp/techstore.dump
check "the backup file exists and is a PostgreSQL custom-format archive" "$(head -c 5 "$WORK/techstore-backup.dump")" "PGDMP"
check "the backup lists the tables" "$(docker run --rm -v "$WORK:/b:ro" postgres:17-alpine pg_restore -l /b/techstore-backup.dump | grep -c 'TABLE DATA public \(products\|orders\|users\)')" 3

restore() { # restore <compose function> <backup file> -- the exact commands from README.md
  local c=$1 file=$2
  $c cp "$file" db:/tmp/restore.dump
  $c stop web api >/dev/null 2>&1
  $c exec -T db pg_restore -U techstore -d techstore --clean --if-exists --single-transaction --exit-on-error /tmp/restore.dump
  local status=$?
  $c exec -T db rm /tmp/restore.dump
  $c run --rm migrate >/dev/null 2>&1
  $c start api web >/dev/null 2>&1
  return $status
}

echo "== a damaged backup file changes nothing (restore is all-or-nothing)"
head -c 200 /dev/urandom > "$WORK/garbage.dump"
restore da "$WORK/garbage.dump" >/dev/null 2>&1; status=$?
differs "restoring garbage fails" "$status" 0
wait_ready $PORT_A || bad "A healthy again after the failed restore"
check "A's data is untouched" "$(fingerprint psql_a)" "$FP_A"

echo "== disaster in A: records lost and changed"
psql_a "truncate orders, order_items, inventory_movements cascade; delete from customers; update inventory_balances set on_hand = 0" >/dev/null 2>&1
psql_a "update products set price = 1" >/dev/null
differs "A's data is now different" "$(fingerprint psql_a)" "$FP_A"

echo "== restore into A (same procedure as the README)"
restore da "$WORK/techstore-backup.dump"; check "pg_restore succeeded" "$?" 0
wait_ready $PORT_A || bad "A healthy after restore"
check "every table and account matches the moment of the backup" "$(fingerprint psql_a)" "$FP_A"
check "record counts match" "$(counts psql_a)" "$COUNTS_A"
check "the Admin signs in again with the same password" "$(api_login $PORT_A $WORK/a2.jar $AE $AP)" 200
check "stock is back: 8 available" "$(curl -s -b $WORK/a2.jar "localhost:$PORT_A/api/inventory?warehouseId=$WH" | python3 -c "import sys,json;print([i['available'] for i in json.load(sys.stdin)['items'] if i['sku']=='BACKUP-1'][0])")" 8
check "the shipped order is back" "$(curl -s -b $WORK/a2.jar localhost:$PORT_A/api/orders/$O1 | jid status)" Shipped
resp=$(api_post $PORT_A $WORK/a2.jar /api/inventory/receipts "{\"productId\":$PID,\"warehouseId\":$WH,\"quantity\":5}"); echo "    response: $resp"
check "the app can write again as the limited user (receive 5 more)" "$(printf '%s' "$resp" | jid onHand)" 13
check "the limited user is still limited" "$(psql_a "select rolsuper::int::text||rolcreatedb::int||rolcreaterole::int from pg_roles where rolname='techstore_app'")" 000
check "...and still cannot drop tables" "$(da exec -T -e PGPASSWORD=$(val stacka TECHSTORE_APP_DB_PASSWORD) db psql -h db -U techstore_app -d techstore -tA -c 'drop table products' 2>&1 | grep -c 'permission denied\|must be owner')" 1

echo "== restore the same backup into a brand-new stack B (different secrets, empty database)"
db_ up --build -d >/dev/null 2>&1 || { echo "compose up failed for B"; exit 1; }
wait_ready $PORT_B || bad "stack B healthy"
BE=$(val stackb TECHSTORE_ADMIN_EMAIL)
check "B starts empty apart from its own Admin" "$(counts psql_b)" "0/0/0/0/1"
differs "B's database passwords are not A's" "$(val stackb POSTGRES_PASSWORD)" "$(val stacka POSTGRES_PASSWORD)"
restore db_ "$WORK/techstore-backup.dump"; check "pg_restore into B succeeded" "$?" 0
wait_ready $PORT_B || bad "B healthy after restore"
check "B now holds exactly A's records and accounts" "$(fingerprint psql_b)" "$FP_A"
check "A's Admin signs in to B with A's password" "$(api_login $PORT_B $WORK/b.jar $AE $AP)" 200
check "B's own original Admin is gone (a restore replaces accounts too)" "$(psql_b "select count(*) from users where email='$BE'")" 0
resp=$(api_post $PORT_B $WORK/b.jar /api/inventory/receipts "{\"productId\":$PID,\"warehouseId\":$WH,\"quantity\":1}")
check "B's limited database user works with B's own password (receive 1 more: 8 + 1)" "$(printf '%s' "$resp" | jid onHand)" 9

echo
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
