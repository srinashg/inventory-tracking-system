"""Signs in independent sessions for the benchmarks, through the real login endpoint (CSRF token, cookie session).

Run by perf.sh only (it passes the fixed host and port). Every session is its own login: its own session cookie and CSRF token.
Logins are paced under the API's sign-in rate limit (10 per minute per client address); the limit is never raised or bypassed, and a
429 answer is waited out. The password comes from the environment, never from a command line. The output file holds live session
cookies, so it is written with mode 600 inside the git-ignored results folder and is deleted by "perf.sh down".
"""
import argparse
import http.client
import json
import os
import sys
import time

ROLES = {"admin": "perf.admin@perf.test", "sales": "perf.sales@perf.test", "warehouse": "perf.warehouse@perf.test"}


def parse_cookies(response):
    cookies = {}
    for header in response.msg.get_all("Set-Cookie") or []:
        pair = header.split(";", 1)[0]
        name, _, value = pair.partition("=")
        cookies[name.strip()] = value.strip()
    return cookies


def call(host, port, method, path, cookies, csrf=None, body=None):
    connection = http.client.HTTPConnection(host, port, timeout=30)
    headers = {"Accept": "application/json"}
    if cookies:
        headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items())
    if csrf:
        headers["X-CSRF-TOKEN"] = csrf
    payload = None
    if body is not None:
        payload = json.dumps(body)
        headers["Content-Type"] = "application/json"
    connection.request(method, path, body=payload, headers=headers)
    response = connection.getresponse()
    data = response.read()
    result = (response.status, parse_cookies(response), data, response.getheader("Retry-After"))
    connection.close()
    return result


def sign_in(host, port, email, password):
    cookies = {}
    status, new, data, _ = call(host, port, "GET", "/api/auth/csrf", cookies)
    cookies.update(new)
    token = json.loads(data)["token"]

    status, new, _, retry_after = call(host, port, "POST", "/api/auth/login", cookies, token, {"email": email, "password": password})
    if status == 429:
        return None, int(retry_after or 60)
    if status != 200:
        raise SystemExit(f"sign-in was refused with status {status} (check that the API runs in the Testing environment)")
    cookies.update(new)

    # The CSRF token belongs to one identity, so a signed-in session needs a new one.
    status, new, data, _ = call(host, port, "GET", "/api/auth/csrf", cookies)
    cookies.update(new)
    token = json.loads(data)["token"]

    status, _, _, _ = call(host, port, "GET", "/api/auth/me", cookies)
    if status != 200:
        raise SystemExit(f"the new session was not accepted (status {status})")
    return {"cookies": cookies, "csrf": token}, 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--pace", type=float, required=True)
    parser.add_argument("--check", action="store_true", help="only verify that the sessions in --out are still accepted")
    parser.add_argument("requests", nargs="*", help="COUNT:ROLE, for example 10:sales")
    args = parser.parse_args()

    if args.host not in ("127.0.0.1", "localhost") or args.port != 5611:
        raise SystemExit("refusing to sign in anywhere except the isolated performance API on 127.0.0.1:5611")
    if "/.results/" not in os.path.abspath(args.out):
        raise SystemExit("the session file must live inside the git-ignored .results folder")
    if args.check:
        with open(args.out) as handle:
            saved = json.load(handle)["sessions"]
        bad = 0
        for session in saved:
            status, _, _, _ = call(args.host, args.port, "GET", "/api/auth/me", session["cookies"])
            bad += status != 200
        print(f"{len(saved) - bad} of {len(saved)} saved sessions are accepted")
        return 1 if bad else 0
    if not args.requests:
        raise SystemExit("nothing to do: give COUNT:ROLE arguments")
    password = os.environ.get("PERF_ACCOUNT_PASSWORD")
    if not password:
        raise SystemExit("PERF_ACCOUNT_PASSWORD is not set")

    wanted = []
    for item in args.requests:
        count, _, role = item.partition(":")
        if not count.isdigit() or role not in ROLES:
            raise SystemExit(f"bad request '{item}': use COUNT:ROLE with a role of admin, sales or warehouse")
        wanted += [role] * int(count)

    sessions = []
    for index, role in enumerate(wanted):
        if index:
            time.sleep(args.pace)
        while True:
            session, wait = sign_in(args.host, args.port, ROLES[role], password)
            if session:
                break
            print(f"  rate limited: waiting {wait + 1} s", flush=True)
            time.sleep(wait + 1)
        session["role"] = role
        sessions.append(session)
        print(f"  session {len(sessions)}/{len(wanted)} ({role}) signed in", flush=True)

    descriptor = os.open(args.out, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as handle:
        json.dump({"created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "sessions": sessions}, handle)
    print(f"{len(sessions)} sessions saved (not printed: they are credentials)")


if __name__ == "__main__":
    sys.exit(main())
