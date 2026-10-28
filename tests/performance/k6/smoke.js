// Smoke test (Step 14B): proves the isolated environment works end to end. It is NOT a benchmark and its timings mean nothing:
// one virtual user, one pass over each read endpoint, one reservation, and the checks that the security rules still hold.
import { check } from 'k6'
import { DRAFT_COUNT, DRAFT_FIRST, classify, get, post, postWithoutCsrf, recordRead, recordReservation } from './lib.js'

export const options = {
  scenarios: { smoke: { executor: 'shared-iterations', vus: 1, iterations: 1, maxDuration: '2m' } },
  thresholds: { checks: ['rate==1'], responses_unexpected_error: ['count==0'] },
}

export default function () {
  if (DRAFT_COUNT < 1) throw new Error('no Draft orders: run perf.sh drafts first')

  // Reads, as a Sales user.
  const reads = [
    ['/api/products?page=1&pageSize=50', 'products'],
    ['/api/inventory?warehouseId=1&page=1&pageSize=50', 'inventory'],
    ['/api/orders?status=Reserved&page=1&pageSize=50', 'orders-list'],
    ['/api/orders/1', 'order-detail'],
    ['/api/dashboard/summary', 'dashboard'],
    ['/api/warehouses', 'warehouses'],
    ['/api/customers', 'customers'],
  ]
  for (const [path, name] of reads) {
    const response = get(path, name, 'sales')
    recordRead(response)
    check(response, { [`GET ${name} is 200`]: (r) => r.status === 200 })
  }

  // The fixtures are what the plan says they are.
  const products = get('/api/products?page=1&pageSize=1', 'products-count', 'sales')
  check(products, { 'the catalog holds 10,000 products': (r) => r.json('totalCount') === 10000 })
  const orders = get('/api/orders?page=1&pageSize=1', 'orders-count', 'sales')
  check(orders, { 'there are 1,000 + drafts orders': (r) => r.json('totalCount') === 1000 + DRAFT_COUNT })

  // A reservation through the real API: it must succeed once and then be refused as a conflict (the order is no longer a Draft).
  const first = post(`/api/orders/${DRAFT_FIRST}/submit`, 'submit', 'sales')
  recordReservation(first)
  check(first, { 'submit succeeds': (r) => r.status === 200, 'the order is Reserved': (r) => r.json('status') === 'Reserved' })
  const again = post(`/api/orders/${DRAFT_FIRST}/submit`, 'submit-again', 'sales')
  classify(again, true)
  check(again, { 'a second submit is a 409 conflict': (r) => r.status === 409 })

  // Security still applies: the Warehouse role may not submit orders, and a missing CSRF token is refused.
  const forbidden = post(`/api/orders/${DRAFT_FIRST + 1}/submit`, 'submit-forbidden', 'warehouse')
  check(forbidden, { 'Warehouse cannot submit orders (403)': (r) => r.status === 403 })
  const noToken = postWithoutCsrf(`/api/orders/${DRAFT_FIRST + 1}/submit`, 'submit-no-csrf', 'sales')
  check(noToken, { 'a request without a CSRF token is refused (400)': (r) => r.status === 400 })
}
