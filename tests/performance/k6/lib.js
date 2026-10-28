// Shared helpers for the k6 scripts (Step 14B). Used by every benchmark script so that the safety rules, the session handling and the
// response classification are the same everywhere.
import http from 'k6/http'
import { SharedArray } from 'k6/data'
import { Counter, Rate } from 'k6/metrics'

// ---- Safety: this tooling talks to the isolated performance API and nothing else. --------------------------------------------
const ALLOWED_BASE_URL = 'http://127.0.0.1:5611'
export const BASE_URL = __ENV.BASE_URL
if (BASE_URL !== ALLOWED_BASE_URL) {
  throw new Error(`refusing target '${BASE_URL}': performance scripts only talk to ${ALLOWED_BASE_URL} (use tests/performance/perf.sh run)`)
}
const SESSIONS_FILE = __ENV.SESSIONS_FILE || ''
if (!SESSIONS_FILE.includes('/tests/performance/.results/')) {
  throw new Error('the sessions file must be inside tests/performance/.results (use tests/performance/perf.sh run)')
}

// Signed-in sessions created BEFORE the run by perf.sh (real logins, paced under the rate limit). Nothing signs in during the run.
const sessions = new SharedArray('sessions', () => JSON.parse(open(SESSIONS_FILE)).sessions)
export const DRAFT_FIRST = Number(__ENV.DRAFT_FIRST || 0)
export const DRAFT_COUNT = Number(__ENV.DRAFT_COUNT || 0)

// ---- Response classification: every request is exactly one of these. --------------------------------------------------------
export const successful = new Counter('responses_successful')
export const expectedConflicts = new Counter('responses_expected_conflict') // 409 where the scenario expects it (e.g. insufficient stock)
export const unexpectedErrors = new Counter('responses_unexpected_error') // everything else: 4xx, 5xx, network errors
export const readUnder300 = new Rate('read_under_300ms') // share of SUCCESSFUL reads that took under 300 ms (NFR4)
export const reservationUnder750 = new Rate('reservation_under_750ms') // share of SUCCESSFUL reservations under 750 ms (NFR4)

export function classify(response, expectConflict = false) {
  if (response.status >= 200 && response.status < 300) {
    successful.add(1)
    return 'success'
  }
  if (expectConflict && response.status === 409) {
    expectedConflicts.add(1)
    return 'expected-conflict'
  }
  unexpectedErrors.add(1)
  return 'unexpected-error'
}

// ---- A virtual user's own sessions (one per role it uses): cookie jar and CSRF token, picked by the VU number. ----------------
// Each role keeps its own cookie jar and every request names its jar explicitly, so two roles in one VU never share cookies.
const mine = {}
export function session(role) {
  const key = role || 'any'
  if (!mine[key]) {
    const candidates = role ? sessions.filter((s) => s.role === role) : sessions
    if (candidates.length === 0) throw new Error(`no ${role || ''} session available: run perf.sh sessions first`)
    const chosen = candidates[(__VU - 1) % candidates.length]
    const jar = new http.CookieJar()
    for (const [name, value] of Object.entries(chosen.cookies)) jar.set(BASE_URL, name, value)
    mine[key] = { jar, csrf: chosen.csrf }
  }
  return mine[key]
}

export function get(path, name, role) {
  const s = session(role)
  return http.get(`${BASE_URL}${path}`, { jar: s.jar, tags: { name }, headers: { Accept: 'application/json' } })
}

export function post(path, name, role, body) {
  const s = session(role)
  const headers = { Accept: 'application/json', 'X-CSRF-TOKEN': s.csrf }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  return http.post(`${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), { jar: s.jar, tags: { name }, headers })
}

/** A change request from a signed-in session that leaves out the CSRF token (used only to prove it is refused). */
export function postWithoutCsrf(path, name, role) {
  const s = session(role)
  return http.post(`${BASE_URL}${path}`, null, { jar: s.jar, tags: { name }, headers: { Accept: 'application/json' } })
}

/** Records a read: classification plus, for successful reads only, whether it beat the 300 ms target. */
export function recordRead(response) {
  if (classify(response) === 'success') readUnder300.add(response.timings.duration < 300)
}

/** Records a reservation (order submit): successful ones against the 750 ms target; 409 is an expected conflict. */
export function recordReservation(response) {
  if (classify(response, true) === 'success') reservationUnder750.add(response.timings.duration < 750)
}
