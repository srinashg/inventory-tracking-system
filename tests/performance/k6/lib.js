// Shared helpers for the k6 scripts (Step 14). Used by every script so that the safety rules, the session handling and the
// response classification are the same everywhere.
import exec from 'k6/execution'
import http from 'k6/http'
import { SharedArray } from 'k6/data'
import { Counter, Rate, Trend } from 'k6/metrics'

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

// Every sample carries a "phase" tag: "warmup" (discarded) or "measure" (reported).
export const PHASE_WARMUP = 'warmup'
export const PHASE_MEASURE = 'measure'

// ---- Response classification: every request is exactly one of these. --------------------------------------------------------
export const successful = new Counter('responses_successful')
export const expectedConflicts = new Counter('responses_expected_conflict') // 409 where the scenario expects it (e.g. insufficient stock)
export const unexpectedErrors = new Counter('responses_unexpected_error') // everything else: 4xx, 5xx, network errors
// The time (ms since the test started) at which each request finished: its min and max give the measured window, for throughput.
export const clock = new Trend('measure_clock_ms')
export const readUnder300 = new Rate('read_under_300ms') // share of SUCCESSFUL reads that took under 300 ms (NFR4)
export const reservationUnder750 = new Rate('reservation_under_750ms') // share of SUCCESSFUL reservations under 750 ms (NFR4)

export function classify(response, expectConflict = false, phase = PHASE_MEASURE) {
  const tags = { phase }
  if (response.status >= 200 && response.status < 300) {
    successful.add(1, tags)
    return 'success'
  }
  if (expectConflict && response.status === 409) {
    expectedConflicts.add(1, tags)
    return 'expected-conflict'
  }
  unexpectedErrors.add(1, tags)
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

/** How many sessions of a role the file holds (a run with more virtual users than sessions would share sessions). */
export function sessionCount(role) {
  return sessions.filter((s) => s.role === role).length
}

export function get(path, name, role, phase = PHASE_MEASURE) {
  const s = session(role)
  return http.get(`${BASE_URL}${path}`, { jar: s.jar, tags: { name, phase }, headers: { Accept: 'application/json' } })
}

export function post(path, name, role, body, phase = PHASE_MEASURE) {
  const s = session(role)
  const headers = { Accept: 'application/json', 'X-CSRF-TOKEN': s.csrf }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  return http.post(`${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), { jar: s.jar, tags: { name, phase }, headers })
}

/** A change request from a signed-in session that leaves out the CSRF token (used only to prove it is refused). */
export function postWithoutCsrf(path, name, role) {
  const s = session(role)
  return http.post(`${BASE_URL}${path}`, null, { jar: s.jar, tags: { name }, headers: { Accept: 'application/json' } })
}

/** Records a read: classification plus, for successful reads only, whether it beat the 300 ms target. */
export function recordRead(response, phase = PHASE_MEASURE) {
  clock.add(exec.instance.currentTestRunDuration, { phase })
  if (classify(response, false, phase) === 'success') readUnder300.add(response.timings.duration < 300, { phase })
}

/** Records a reservation (order submit): successful ones against the 750 ms target; 409 is an expected conflict. */
export function recordReservation(response, phase = PHASE_MEASURE) {
  clock.add(exec.instance.currentTestRunDuration, { phase })
  if (classify(response, true, phase) === 'success') reservationUnder750.add(response.timings.duration < 750, { phase })
}

/** A deterministic pseudo-random number in [0, n) from a sequence number and a salt: the same run always asks the same questions. */
export function pick(sequence, salt, n) {
  let h = Math.imul(sequence + 1, 2654435761) ^ Math.imul(salt + 1, 40503)
  h = Math.imul(h ^ (h >>> 15), 2246822519)
  h = Math.imul(h ^ (h >>> 13), 3266489917)
  return ((h ^ (h >>> 16)) >>> 0) % n
}

/** Threshold entries that never fail but make k6 report these sub-metrics (percentiles per phase and per endpoint) in the summary export. */
export function reportOnly(metric, selectors, expression = 'max>=0') {
  const result = {}
  for (const selector of selectors) result[`${metric}{${selector}}`] = [expression]
  return result
}
