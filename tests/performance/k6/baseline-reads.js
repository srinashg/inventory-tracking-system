// NFR4 baseline, read requests (Step 14C). A closed workload: VUS virtual users, each with its OWN signed-in session, send requests
// back to back (no think time) for WARMUP_S seconds of warm-up (discarded) and then MEASURE_S seconds that are reported.
// Timings include HTTP and the session/authorization check, from the client's side, on 127.0.0.1.
//
// The read mix (a proposal approved in Step 14A): products 30%, inventory 25%, order list 15%, order detail 15%, dashboard 10%,
// warehouses 3%, customers 2%. Parameters vary deterministically (same run, same requests). Only successful (2xx) reads can count
// as "fast"; anything else is counted as an error and reported separately.
import exec from 'k6/execution'
import { PHASE_MEASURE, PHASE_WARMUP, get, pick, recordRead, reportOnly, sessionCount } from './lib.js'

const VUS = Number(__ENV.VUS || 10)
const WARMUP_S = Number(__ENV.WARMUP_S || 30)
const MEASURE_S = Number(__ENV.MEASURE_S || 60)
const ENDPOINTS = ['products', 'inventory', 'orders-list', 'order-detail', 'dashboard', 'warehouses', 'customers']
const STATUSES = ['Draft', 'Reserved', 'Picking', 'Packed', 'Shipped', 'Canceled']

export const options = {
  scenarios: { reads: { executor: 'constant-vus', vus: VUS, duration: `${WARMUP_S + MEASURE_S}s` } },
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  thresholds: {
    ...reportOnly('http_req_duration', ['phase:measure,expected_response:true', ...ENDPOINTS.map((e) => `phase:measure,name:${e},expected_response:true`)]),
    ...reportOnly('http_reqs', ['phase:measure'], 'count>=0'),
    ...reportOnly('measure_clock_ms', ['phase:measure']),
    ...reportOnly('read_under_300ms', ['phase:measure'], 'rate>=0'),
    ...reportOnly('responses_successful', ['phase:measure'], 'count>=0'),
    ...reportOnly('responses_expected_conflict', ['phase:measure'], 'count>=0'),
    ...reportOnly('responses_unexpected_error', ['phase:measure'], 'count>=0'),
  },
}

export function setup() {
  if (sessionCount('sales') < VUS) throw new Error(`${VUS} virtual users need ${VUS} sales sessions (the file has ${sessionCount('sales')})`)
}

export default function () {
  const phase = exec.instance.currentTestRunDuration < WARMUP_S * 1000 ? PHASE_WARMUP : PHASE_MEASURE
  const n = exec.scenario.iterationInTest
  const slot = pick(n, 1, 100)
  let response
  if (slot < 30) {
    const active = pick(n, 2, 2) === 0 ? '&activeOnly=true' : ''
    response = get(`/api/products?page=${1 + pick(n, 3, 200)}&pageSize=50${active}`, 'products', 'sales', phase)
  } else if (slot < 55) {
    response = get(`/api/inventory?warehouseId=${1 + pick(n, 4, 3)}&page=${1 + pick(n, 5, 130)}&pageSize=50`, 'inventory', 'sales', phase)
  } else if (slot < 70) {
    const status = pick(n, 6, 7) === 6 ? '' : `&status=${STATUSES[pick(n, 7, 6)]}`
    response = get(`/api/orders?page=${1 + pick(n, 8, 4)}&pageSize=50${status}`, 'orders-list', 'sales', phase)
  } else if (slot < 85) {
    response = get(`/api/orders/${1 + pick(n, 9, 1000)}`, 'order-detail', 'sales', phase)
  } else if (slot < 95) {
    response = get('/api/dashboard/summary', 'dashboard', 'sales', phase)
  } else if (slot < 98) {
    response = get('/api/warehouses', 'warehouses', 'sales', phase)
  } else {
    response = get('/api/customers', 'customers', 'sales', phase)
  }
  recordRead(response, phase)
}
