// The one place that talks to the backend. Every function matches an endpoint in docs/api-contract.md.
// The UI never builds URLs or parses errors itself: it calls these functions and shows what comes back.
import type {
  AdjustStockRequest,
  CreateOrderRequest,
  CreateProductRequest,
  CurrentUser,
  Customer,
  DashboardSummary,
  InventoryItem,
  Order,
  OrderListItem,
  OrderStatus,
  Paged,
  Product,
  ProblemDetails,
  ReceiveStockRequest,
  StockChange,
  UpdateProductRequest,
  Warehouse,
} from '../types'

const GENERIC_MESSAGE = 'Something went wrong. Please try again.'

/** A failed API call. `status` is 0 when the server could not be reached at all. */
export class ApiError extends Error {
  status: number
  title: string
  detail?: string
  errors?: Record<string, string[]>

  constructor(status: number, title: string, message: string, detail?: string, errors?: Record<string, string[]>) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
    this.errors = errors
  }
}

/** The message to show a person: the server's own explanation when there is one. */
export function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : GENERIC_MESSAGE
}

/** Field name -> first problem, for showing messages next to form fields. Names are normalized to camelCase. */
export function fieldErrors(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError) || !error.errors) {
    return {}
  }

  const result: Record<string, string> = {}
  for (const [key, messages] of Object.entries(error.errors)) {
    const name = key.replace(/^\$\./, '').replace(/^./, (c) => c.toLowerCase())
    if (messages.length > 0) {
      result[name] = messages[0]
    }
  }
  return result
}

function messageFromProblem(problem: ProblemDetails): string {
  if (problem.errors) {
    const messages = Object.values(problem.errors).flat()
    if (messages.length > 0) {
      return messages.join(' ')
    }
  }
  return problem.detail ?? problem.title
}

// ---- Session and CSRF -----------------------------------------------------------------------------------------
// The session itself is an HttpOnly cookie that the browser manages; JavaScript never sees it. The only thing kept here
// is the anti-forgery token, in memory (never in localStorage), which the server requires on every POST and PATCH.

let csrfToken: string | undefined
const unauthorizedListeners = new Set<() => void>()

/** Forgets the cached CSRF token. Used by tests, and after the signed-in identity changes. */
export function resetAuthState(): void {
  csrfToken = undefined
}

/** Calls `listener` whenever the server answers 401 to a request, meaning the session has ended. Returns an unsubscribe function. */
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener)
  return () => {
    unauthorizedListeners.delete(listener)
  }
}

async function send(method: string, url: string, body: unknown, token?: string): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (token !== undefined) {
    headers['X-CSRF-TOKEN'] = token
  }

  try {
    return await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  } catch {
    throw new ApiError(0, 'Network error', 'Cannot reach the server. Check that the API is running and try again.')
  }
}

async function getCsrfToken(): Promise<string> {
  if (csrfToken === undefined) {
    const response = await send('GET', '/api/auth/csrf', undefined)
    if (!response.ok) {
      throw new ApiError(response.status, response.statusText, 'Could not start a secure session. Please try again.')
    }
    csrfToken = ((await response.json()) as { token: string }).token
  }
  return csrfToken
}

const changesData = (method: string) => method !== 'GET' && method !== 'HEAD'
const isTokenProblem = (problem: ProblemDetails) => problem.status === 400 && /anti-forgery/i.test(problem.detail ?? '')

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  let response = await send(method, url, body, changesData(method) ? await getCsrfToken() : undefined)

  // A refused token (for example after the session changed in another tab): get a fresh one and try once more.
  if (changesData(method) && response.status === 400 && (await readsAsTokenProblem(response))) {
    csrfToken = undefined
    response = await send(method, url, body, await getCsrfToken())
  }

  // Signing in or out changes the identity the token belongs to, so the next change needs a new token.
  if (url === '/api/auth/login' || url === '/api/auth/logout') {
    csrfToken = undefined
  }

  if (response.ok) {
    return (response.status === 204 ? undefined : await response.json()) as T
  }

  const isJson = response.headers.get('Content-Type')?.includes('json') ?? false
  if (isJson) {
    const problem = (await response.json()) as ProblemDetails
    if (response.status === 401 && url !== '/api/auth/login') {
      unauthorizedListeners.forEach((listener) => listener())
    }
    throw new ApiError(response.status, problem.title, messageFromProblem(problem), problem.detail, problem.errors)
  }

  throw new ApiError(
    response.status,
    response.statusText,
    `The server returned an unexpected response (${response.status}). Please try again.`,
  )
}

/** Looks at a 400 body without using it up, to see whether it is the "bad anti-forgery token" answer. */
async function readsAsTokenProblem(response: Response): Promise<boolean> {
  if (!(response.headers.get('Content-Type')?.includes('json') ?? false)) return false
  return isTokenProblem((await response.clone().json()) as ProblemDetails)
}

type QueryValue = string | number | boolean | undefined | readonly (string | number)[]

/** Builds "?a=1&b=2". Parameters that are undefined are left out; an array repeats the name (status=A&status=B). */
function query(params: Record<string, QueryValue>): string {
  const pairs: string[] = []
  for (const [key, value] of Object.entries(params)) {
    const values = Array.isArray(value) ? value : [value]
    for (const item of values) {
      if (item !== undefined) {
        pairs.push(`${key}=${encodeURIComponent(String(item))}`)
      }
    }
  }
  return pairs.length > 0 ? `?${pairs.join('&')}` : ''
}

export const api = {
  // Session
  me: () => request<CurrentUser>('GET', '/api/auth/me'),
  login: (email: string, password: string) => request<CurrentUser>('POST', '/api/auth/login', { email, password }),
  logout: () => request<void>('POST', '/api/auth/logout'),

  // Products
  listProducts: (params: { activeOnly?: boolean; page?: number; pageSize?: number } = {}) =>
    request<Paged<Product>>('GET', `/api/products${query(params)}`),
  createProduct: (body: CreateProductRequest) => request<Product>('POST', '/api/products', body),
  updateProduct: (id: number, body: UpdateProductRequest) => request<Product>('PATCH', `/api/products/${id}`, body),

  // Reference data
  listWarehouses: () => request<Warehouse[]>('GET', '/api/warehouses'),
  listCustomers: () => request<Customer[]>('GET', '/api/customers'),

  // Inventory
  getInventory: (warehouseId: number, page?: number, pageSize?: number) =>
    request<Paged<InventoryItem>>('GET', `/api/inventory${query({ warehouseId, page, pageSize })}`),
  receiveStock: (body: ReceiveStockRequest) => request<StockChange>('POST', '/api/inventory/receipts', body),
  adjustStock: (body: AdjustStockRequest) => request<StockChange>('POST', '/api/inventory/adjustments', body),

  // Orders
  listOrders: (params: { status?: OrderStatus[]; customerId?: number; warehouseId?: number; page?: number; pageSize?: number } = {}) =>
    request<Paged<OrderListItem>>('GET', `/api/orders${query(params)}`),
  createOrder: (body: CreateOrderRequest) => request<Order>('POST', '/api/orders', body),
  getOrder: (id: number) => request<Order>('GET', `/api/orders/${id}`),
  submitOrder: (id: number) => request<Order>('POST', `/api/orders/${id}/submit`),
  pickOrder: (id: number) => request<Order>('POST', `/api/orders/${id}/pick`),
  packOrder: (id: number) => request<Order>('POST', `/api/orders/${id}/pack`),
  shipOrder: (id: number) => request<Order>('POST', `/api/orders/${id}/ship`),
  cancelOrder: (id: number) => request<Order>('POST', `/api/orders/${id}/cancel`),

  // Dashboard
  getDashboardSummary: () => request<DashboardSummary>('GET', '/api/dashboard/summary'),
}
