import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api, errorMessage, fieldErrors, onUnauthorized, resetAuthState } from './client'

const json = (status: number, body?: unknown, contentType = 'application/json') =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: body === undefined ? {} : { 'Content-Type': contentType } })

/** Makes fetch() answer like the API: the CSRF endpoint gives a token, every other call gets the given response. */
function respondWith(status: number, body?: unknown, contentType = 'application/json') {
  let csrfCount = 0
  const fetchMock = vi.fn(async (url: string) =>
    url === '/api/auth/csrf' ? json(200, { token: `token-${++csrfCount}` }) : json(status, body, contentType),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

/** The calls that are not CSRF-token fetches. */
const apiCalls = (fetchMock: ReturnType<typeof vi.fn>) => fetchMock.mock.calls.filter(([url]) => url !== '/api/auth/csrf')

beforeEach(() => {
  vi.unstubAllGlobals()
  resetAuthState()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('api client requests', () => {
  it('lists products with query parameters and returns the parsed page', async () => {
    const page = { items: [{ productId: 1, sku: 'LAP-1', name: 'Laptop', price: 10, isActive: true }], page: 2, pageSize: 5, totalCount: 6 }
    const fetchMock = respondWith(200, page)

    const result = await api.listProducts({ activeOnly: true, page: 2, pageSize: 5 })

    expect(fetchMock).toHaveBeenCalledWith('/api/products?activeOnly=true&page=2&pageSize=5', expect.objectContaining({ method: 'GET' }))
    expect(result).toEqual(page)
  })

  it('posts a JSON body with a JSON content type when creating a product', async () => {
    const fetchMock = respondWith(201, { productId: 7, sku: 'LAP-1', name: 'Laptop', price: 10, isActive: true })

    const product = await api.createProduct({ sku: 'LAP-1', name: 'Laptop', price: 10 })

    const [url, init] = apiCalls(fetchMock)[0]
    expect(url).toBe('/api/products')
    expect(init.method).toBe('POST')
    expect(init.headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(init.body)).toEqual({ sku: 'LAP-1', name: 'Laptop', price: 10 })
    expect(product.productId).toBe(7)
  })

  it('uses PATCH for product updates', async () => {
    const fetchMock = respondWith(200, { productId: 7, sku: 'LAP-1', name: 'New', price: 10, isActive: false })

    await api.updateProduct(7, { name: 'New', isActive: false })

    expect(fetchMock).toHaveBeenCalledWith('/api/products/7', expect.objectContaining({ method: 'PATCH' }))
  })

  it('lists orders with a repeated status parameter, filters and paging', async () => {
    const page = { items: [], page: 2, pageSize: 10, totalCount: 0 }
    const fetchMock = respondWith(200, page)

    const result = await api.listOrders({ status: ['Reserved', 'Picking', 'Packed'], customerId: 4, warehouseId: 2, page: 2, pageSize: 10 })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/orders?status=Reserved&status=Picking&status=Packed&customerId=4&warehouseId=2&page=2&pageSize=10',
      expect.objectContaining({ method: 'GET' }),
    )
    expect(result).toEqual(page)
  })

  it('lists every order when no filters are given', async () => {
    const fetchMock = respondWith(200, { items: [], page: 1, pageSize: 50, totalCount: 0 })

    await api.listOrders()

    expect(fetchMock).toHaveBeenCalledWith('/api/orders', expect.objectContaining({ method: 'GET' }))
  })

  it('leaves out an empty status list', async () => {
    const fetchMock = respondWith(200, { items: [], page: 1, pageSize: 50, totalCount: 0 })

    await api.listOrders({ status: [], page: 1 })

    expect(fetchMock).toHaveBeenCalledWith('/api/orders?page=1', expect.anything())
  })

  it('calls order actions with POST and no body', async () => {
    const fetchMock = respondWith(200, { orderId: 5, status: 'Reserved', items: [] })

    await api.submitOrder(5)

    const [url, init] = apiCalls(fetchMock)[0]
    expect(url).toBe('/api/orders/5/submit')
    expect(init.method).toBe('POST')
    expect(init.body).toBeUndefined()
  })

  it('maps every action to its documented endpoint', async () => {
    const fetchMock = respondWith(200, {})
    
    await api.pickOrder(1)
    await api.packOrder(1)
    await api.shipOrder(1)
    await api.cancelOrder(1)
    await api.getOrder(1)
    await api.listOrders({ page: 3 })
    await api.getInventory(3, 2, 10)
    await api.receiveStock({ productId: 1, warehouseId: 2, quantity: 3 })
    await api.adjustStock({ productId: 1, warehouseId: 2, quantityChange: -1, reason: 'x' })
    await api.listWarehouses()
    await api.listCustomers()
    await api.getDashboardSummary()

    const calls = apiCalls(fetchMock).map(([url, init]) => `${init.method} ${url}`)
    expect(calls).toEqual([
      'POST /api/orders/1/pick',
      'POST /api/orders/1/pack',
      'POST /api/orders/1/ship',
      'POST /api/orders/1/cancel',
      'GET /api/orders/1',
      'GET /api/orders?page=3',
      'GET /api/inventory?warehouseId=3&page=2&pageSize=10',
      'POST /api/inventory/receipts',
      'POST /api/inventory/adjustments',
      'GET /api/warehouses',
      'GET /api/customers',
      'GET /api/dashboard/summary',
    ])
  })
})

describe('api client errors', () => {
  it('turns a 409 problem response into an ApiError carrying the server message', async () => {
    respondWith(409, { status: 409, title: 'Conflict', detail: 'Insufficient stock for SKU LAP-1: requested 15, available 10.' }, 'application/problem+json')

    const error = await api.submitOrder(1).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(409)
    expect(errorMessage(error)).toBe('Insufficient stock for SKU LAP-1: requested 15, available 10.')
  })

  it('keeps the per-field errors of a 400 response', async () => {
    respondWith(400, { status: 400, title: 'Validation failed', detail: 'One or more fields are invalid.', errors: { sku: ['SKU is required.'], price: ['Price must be positive.'] } }, 'application/problem+json')

    const error = await api.createProduct({ sku: '', name: 'x', price: -1 }).catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(400)
    expect(fieldErrors(error)).toEqual({ sku: 'SKU is required.', price: 'Price must be positive.' })
    expect(errorMessage(error)).toContain('SKU is required.')
  })

  it('normalizes field names such as "$.price" and "Price" to "price"', async () => {
    respondWith(400, { status: 400, title: 'Validation failed', errors: { '$.price': ['bad json'], Sku: ['required'] } }, 'application/problem+json')

    const error = await api.createProduct({ sku: '', name: 'x', price: 1 }).catch((e: unknown) => e)

    expect(fieldErrors(error)).toEqual({ price: 'bad json', sku: 'required' })
  })

  it('reports a network failure as a readable error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const error = await api.listWarehouses().catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(0)
    expect(errorMessage(error)).toMatch(/cannot reach the server/i)
  })

  it('handles an error response that is not JSON', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>Bad gateway</html>', { status: 502, headers: { 'Content-Type': 'text/html' } })))

    const error = await api.listWarehouses().catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(502)
    expect(errorMessage(error)).toMatch(/unexpected/i)
    expect(errorMessage(error)).not.toContain('<html>')
  })

  it('gives a generic message for values that are not ApiErrors', () => {
    expect(errorMessage(new Error('boom'))).toBe('Something went wrong. Please try again.')
    expect(errorMessage('weird')).toBe('Something went wrong. Please try again.')
    expect(fieldErrors(new Error('boom'))).toEqual({})
  })
})

describe('api client sign-in and CSRF', () => {
  it('does not fetch a CSRF token for GET requests', async () => {
    const fetchMock = respondWith(200, [])

    await api.listWarehouses()

    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual(['/api/warehouses'])
  })

  it('fetches a CSRF token once and sends it on every POST and PATCH', async () => {
    const fetchMock = respondWith(200, {})

    await api.submitOrder(1)
    await api.updateProduct(2, { name: 'x' })
    await api.cancelOrder(3)

    expect(fetchMock.mock.calls.filter(([url]) => url === '/api/auth/csrf')).toHaveLength(1)
    const headers = apiCalls(fetchMock).map(([, init]) => init.headers['X-CSRF-TOKEN'])
    expect(headers).toEqual(['token-1', 'token-1', 'token-1'])
  })

  it('fetches a fresh CSRF token after logging in', async () => {
    const fetchMock = respondWith(200, { userId: 1, email: 'a@b.c', displayName: 'A', roles: ['Sales'], permissions: [] })

    await api.submitOrder(1) // token-1
    await api.login('a@b.c', 'a-long-password-1')
    await api.submitOrder(2)

    const sent = apiCalls(fetchMock).map(([url, init]) => `${url} ${init.headers['X-CSRF-TOKEN']}`)
    expect(sent).toEqual(['/api/orders/1/submit token-1', '/api/auth/login token-1', '/api/orders/2/submit token-2'])
  })

  it('fetches a fresh CSRF token after logging out', async () => {
    const fetchMock = respondWith(204)

    await api.logout() // token-1
    await api.logout() // token-2

    expect(apiCalls(fetchMock).map(([, init]) => init.headers['X-CSRF-TOKEN'])).toEqual(['token-1', 'token-2'])
  })

  it('sends the login credentials in the body and nowhere else', async () => {
    const fetchMock = respondWith(200, { userId: 1, email: 'a@b.c', displayName: 'A', roles: [], permissions: [] })

    await api.login('a@b.c', 'a-long-password-1')

    const [url, init] = apiCalls(fetchMock)[0]
    expect(url).toBe('/api/auth/login')
    expect(JSON.parse(init.body)).toEqual({ email: 'a@b.c', password: 'a-long-password-1' })
    expect(JSON.stringify(init.headers)).not.toContain('a-long-password-1')
  })

  it('treats a 204 response as success with no body', async () => {
    respondWith(204)

    await expect(api.logout()).resolves.toBeUndefined()
  })

  it('refreshes the token and retries once when the server says the token is invalid', async () => {
    let posts = 0
    let tokens = 0
    const fetchMock = vi.fn(async (url: string) => {
      if (url === '/api/auth/csrf') return json(200, { token: `token-${++tokens}` })
      return ++posts === 1
        ? json(400, { status: 400, title: 'Bad Request', detail: 'The anti-forgery token is missing or invalid. Reload the page and try again.' }, 'application/problem+json')
        : json(200, { orderId: 1 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const result = await api.submitOrder(1)

    expect(result).toEqual({ orderId: 1 })
    expect(apiCalls(fetchMock).map(([, init]) => init.headers['X-CSRF-TOKEN'])).toEqual(['token-1', 'token-2'])
  })

  it('does not retry forever when the token keeps being refused', async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url === '/api/auth/csrf'
        ? json(200, { token: 't' })
        : json(400, { status: 400, title: 'Bad Request', detail: 'The anti-forgery token is missing or invalid.' }, 'application/problem+json'),
    )
    vi.stubGlobal('fetch', fetchMock)

    const error = await api.submitOrder(1).catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(400)
    expect(apiCalls(fetchMock)).toHaveLength(2)
  })

  it('tells listeners when a request is refused because the session has ended (401)', async () => {
    respondWith(401, { status: 401, title: 'Unauthorized', detail: 'Sign in to continue.' }, 'application/problem+json')
    const listener = vi.fn()
    const stop = onUnauthorized(listener)

    const error = await api.listProducts().catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(401)
    expect(listener).toHaveBeenCalledTimes(1)
    stop()
  })

  it('does not announce a session ending when a login attempt itself fails with 401', async () => {
    respondWith(401, { status: 401, title: 'Unauthorized', detail: 'Invalid email or password.' }, 'application/problem+json')
    const listener = vi.fn()
    const stop = onUnauthorized(listener)

    const error = await api.login('a@b.c', 'wrong-password-123').catch((e: unknown) => e)

    expect(errorMessage(error)).toBe('Invalid email or password.')
    expect(listener).not.toHaveBeenCalled()
    stop()
  })

  it('stops notifying a listener once it has unsubscribed', async () => {
    respondWith(401, { status: 401, title: 'Unauthorized', detail: 'Sign in to continue.' }, 'application/problem+json')
    const listener = vi.fn()
    onUnauthorized(listener)()

    await api.listProducts().catch(() => undefined)

    expect(listener).not.toHaveBeenCalled()
  })

  it('shows the server message for a 403 (the role does not allow the action)', async () => {
    respondWith(403, { status: 403, title: 'Forbidden', detail: 'Your role does not allow this action.' }, 'application/problem+json')

    const error = await api.submitOrder(1).catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(403)
    expect(errorMessage(error)).toBe('Your role does not allow this action.')
  })

  it('reads the signed-in user from /api/auth/me', async () => {
    const user = { userId: 3, email: 'a@b.c', displayName: 'A', roles: ['Sales'], permissions: ['ManageOrders'] }
    const fetchMock = respondWith(200, user)

    expect(await api.me()).toEqual(user)
    expect(fetchMock).toHaveBeenCalledWith('/api/auth/me', expect.objectContaining({ method: 'GET' }))
  })

  it('never writes anything to localStorage or sessionStorage', async () => {
    respondWith(200, { userId: 1, email: 'a@b.c', displayName: 'A', roles: [], permissions: [] })
    const setItem = vi.spyOn(Storage.prototype, 'setItem')

    await api.login('a@b.c', 'a-long-password-1')
    await api.submitOrder(1)

    expect(setItem).not.toHaveBeenCalled()
    setItem.mockRestore()
  })
})
