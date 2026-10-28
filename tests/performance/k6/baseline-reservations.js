// NFR4 baseline, reservation requests (Step 14C). VUS virtual users, each with its OWN Sales session, submit fresh Draft orders
// (POST /api/orders/{id}/submit) back to back: first WARMUP_N submits (discarded), then MEASURE_N submits (reported). Every order has its
// own stock rows and plenty of stock, so a submit is expected to succeed; a 409 or anything else is reported separately, never counted
// as a fast success. Timings include HTTP, the session check and the CSRF check, from the client's side, on 127.0.0.1.
import exec from 'k6/execution'
import { DRAFT_COUNT, DRAFT_FIRST, PHASE_MEASURE, PHASE_WARMUP, post, recordReservation, reportOnly, sessionCount } from './lib.js'

const VUS = Number(__ENV.VUS || 10)
const WARMUP_N = Number(__ENV.WARMUP_N || 200)
const MEASURE_N = Number(__ENV.MEASURE_N || 1000)

export const options = {
  scenarios: { reservations: { executor: 'shared-iterations', vus: VUS, iterations: WARMUP_N + MEASURE_N, maxDuration: '20m' } },
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  thresholds: {
    ...reportOnly('http_req_duration', ['phase:measure,expected_response:true']),
    ...reportOnly('http_reqs', ['phase:measure'], 'count>=0'),
    ...reportOnly('measure_clock_ms', ['phase:measure']),
    ...reportOnly('reservation_under_750ms', ['phase:measure'], 'rate>=0'),
    ...reportOnly('responses_successful', ['phase:measure'], 'count>=0'),
    ...reportOnly('responses_expected_conflict', ['phase:measure'], 'count>=0'),
    ...reportOnly('responses_unexpected_error', ['phase:measure'], 'count>=0'),
  },
}

export function setup() {
  if (sessionCount('sales') < VUS) throw new Error(`${VUS} virtual users need ${VUS} sales sessions (the file has ${sessionCount('sales')})`)
  if (DRAFT_COUNT < WARMUP_N + MEASURE_N) throw new Error(`need ${WARMUP_N + MEASURE_N} fresh Draft orders, found ${DRAFT_COUNT}: run perf.sh reset and drafts first`)
}

export default function () {
  const n = exec.scenario.iterationInTest // 0, 1, 2 ...: each request submits a different Draft order
  const phase = n < WARMUP_N ? PHASE_WARMUP : PHASE_MEASURE
  recordReservation(post(`/api/orders/${DRAFT_FIRST + n}/submit`, 'submit', 'sales', undefined, phase), phase)
}
