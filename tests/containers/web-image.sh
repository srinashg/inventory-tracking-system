#!/usr/bin/env bash
# Container test for the web image (Step 11C). Builds frontend/Dockerfile, runs it next to a stand-in API on a private Docker
# network, and checks what the browser would see. It never touches the development database or any real secret.
#   Usage (from the repository root):  bash tests/containers/web-image.sh
set -u
cd "$(dirname "$0")/../.."

NET=ts-webtest-net; WEB=ts-webtest-web; UP=ts-webtest-api; PORT=58081
pass=0; fail=0
ok()   { pass=$((pass+1)); echo "  ok   $1"; }
bad()  { fail=$((fail+1)); echo "  FAIL $1"; [ -n "${2:-}" ] && echo "       $2"; }
check() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3] got [$2]"; fi; }
has()  { if printf '%s' "$2" | grep -qi -- "$3"; then ok "$1"; else bad "$1" "[$3] not found in: $(printf '%s' "$2" | head -c 300)"; fi; }
hasnt(){ if printf '%s' "$2" | grep -qi -- "$3"; then bad "$1" "[$3] found"; else ok "$1"; fi; }

# shellcheck source=tests/containers/ci-support.sh
. tests/containers/ci-support.sh

cleanup() {
  local rc=$?
  if [ -n "${CI_LOG_DIR:-}" ] && { [ "$fail" -gt 0 ] || [ "$rc" -ne 0 ]; }; then
    { docker logs "$WEB" 2>&1; docker logs "$UP" 2>&1; } | ci_save_logs web-image
  fi
  docker rm -f "$WEB" "$UP" >/dev/null 2>&1; docker network rm "$NET" >/dev/null 2>&1
}
trap cleanup EXIT
cleanup

start_upstream() {
  docker run -d --name "$UP" --network "$NET" -v "$PWD/tests/containers/echo-upstream.conf:/etc/nginx/conf.d/default.conf:ro" nginx:alpine >/dev/null
}

echo "== building the image"
docker build -q -t techstore-web:test frontend >/dev/null || { echo "build failed"; exit 1; }
docker network create "$NET" >/dev/null
start_upstream
docker run -d --name "$WEB" --network "$NET" -p 127.0.0.1:$PORT:8080 -e API_UPSTREAM=$UP:8080 techstore-web:test >/dev/null
for _ in $(seq 30); do curl -sf localhost:$PORT/healthz >/dev/null && break; sleep 1; done
U=localhost:$PORT

echo "== the web server itself"
check "/healthz answers without asking the API" "$(curl -s -o /dev/null -w '%{http_code}' $U/healthz)" 200
check "runs as a non-root user" "$([ "$(docker exec $WEB id -u)" != 0 ] && echo yes)" yes
check "does not announce its version" "$(curl -sI $U/ | grep -i '^server:' | tr -d '\r')" "Server: nginx"

echo "== client-side routing: every route (and a refresh on it) returns the app"
for route in / /login /orders /orders/42 /products /inventory /fulfillment /some/unknown/page; do
  body=$(curl -s -w '\n%{http_code} %{content_type}' $U$route)
  has "GET $route" "$body" 'id="root"'
  has "GET $route is 200 html" "$body" '200 text/html'
done

echo "== static files"
js=$(curl -s $U/ | grep -o '/assets/[^"]*\.js' | head -1)
css=$(curl -s $U/ | grep -o '/assets/[^"]*\.css' | head -1)
hdr=$(curl -sI $U$js)
has   "the script is served" "$hdr" 'HTTP/1.1 200'
has   "...as javascript" "$hdr" 'content-type: .*javascript'
has   "...cached for a year (hashed name)" "$hdr" 'cache-control: public, max-age=31536000, immutable'
has   "the stylesheet is served" "$(curl -sI $U$css)" 'content-type: text/css'
check "a missing asset is a real 404, not the app" "$(curl -s -o /dev/null -w '%{http_code}' $U/assets/missing.js)" 404
has   "index.html is never cached" "$(curl -sI $U/login)" 'cache-control: no-cache'
hdr=$(curl -sI $U/login)
has   "CSP blocks framing" "$hdr" "content-security-policy:.*frame-ancestors 'none'"
has   "CSP only allows own scripts" "$hdr" "script-src 'self'"
has   "nosniff" "$hdr" 'x-content-type-options: nosniff'
has   "X-Frame-Options" "$hdr" 'x-frame-options: DENY'
has   "Referrer-Policy" "$hdr" 'referrer-policy: no-referrer'

echo "== no internal address in the code the browser downloads"
all=$(curl -s $U$js; curl -s $U/)
# (react-router contains the harmless placeholder URL "http://localhost" for parsing paths; an address WITH a port is a real target.)
hasnt "no loopback address, dev-server port or localhost:port" "$all" 'localhost:[0-9]\|127\.0\.0\.1\|:5111'
hasnt "no API container name or port" "$all" "$UP\|api:8080"

echo "== forwarding to the API"
echo_json=$(curl -s "$U/api/products?page=2&x=1")
has   "/api/... reaches the API with its query string" "$echo_json" '"uri":"/api/products?page=2&x=1"'
has   "/health reaches the API" "$(curl -s $U/health)" '"uri":"/health"'
check "POST is forwarded as POST" "$(curl -s -X POST $U/api/auth/login | grep -o '"method":"[A-Z]*"')" '"method":"POST"'
hasnt "nginx adds no headers of its own to API answers" "$(curl -sI $U/api/products)" 'content-security-policy'

echo "== X-Forwarded-For is overwritten, never appended to"
honest=$(curl -s $U/api/x | grep -o '"xff":"[^"]*"')
forged=$(curl -s -H 'X-Forwarded-For: 6.6.6.6, 7.7.7.7' $U/api/x | grep -o '"xff":"[^"]*"')
check "a forged header is replaced by the real connecting address" "$forged" "$honest"
hasnt "no forged address survives" "$forged" '6\.6\.6\.6\|7\.7\.7\.7\|,'
forged_real=$(curl -s -H 'X-Real-IP: 8.8.8.8' $U/api/x | grep -o '"realip":"[^"]*"')
hasnt "a forged X-Real-IP does not survive either" "$forged_real" '8\.8\.8\.8'

echo "== cookies and CSRF pass through the proxy untouched"
resp=$(curl -s -D - -H 'Cookie: techstore.session=abc; techstore.csrf=def' -H 'X-CSRF-TOKEN: tok123' $U/api/orders)
has   "request cookies reach the API" "$resp" '"cookie":"techstore.session=abc; techstore.csrf=def"'
has   "the CSRF header reaches the API" "$resp" '"csrf":"tok123"'
has   "Set-Cookie comes back with HttpOnly" "$resp" 'set-cookie: techstore.session=fake; path=/; samesite=strict; httponly'
has   "the API's cache rule is kept" "$resp" 'cache-control: no-store'

echo "== the API container being replaced"
docker rm -f $UP >/dev/null
down=$(curl -s -o /dev/null -w '%{http_code}' $U/api/x)
check "API down: a gateway error (502/504), not a crash" "$([ "$down" = 502 ] || [ "$down" = 504 ] && echo gateway-error)" gateway-error
check "API down: the web server itself and the app keep working" "$(curl -s -o /dev/null -w '%{http_code}' $U/healthz) $(curl -s -o /dev/null -w '%{http_code}' $U/login)" "200 200"
start_upstream
sleep 12  # the web server looks the name up again every 10 seconds
check "API back (new container): proxying resumes without restarting the web server" "$(curl -s -o /dev/null -w '%{http_code}' $U/api/x)" 200

echo
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ]
