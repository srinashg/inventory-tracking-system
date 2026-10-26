import type { Browser } from '@playwright/test'
import { anonymousApi, openAs, type AccountKey } from './support/accounts.ts'
import { resetDatabase } from './support/database.ts'
import { expect, test } from './support/fixtures.ts'

// What each role may see and do. The screen only hides what a role may not do; the API refuses it with 403 anyway,
// and refuses everyone who is not signed in with 401.

test.beforeEach(() => {
  resetDatabase()
})

type Role = Extract<AccountKey, 'admin' | 'sales' | 'warehouse'>

/** What each role sees on each page: [region that should exist, or the hint that replaces it]. */
const VISIBLE: Record<Role, { productForm: boolean; receive: boolean; adjust: boolean; newOrder: boolean; fulfil: boolean }> = {
  admin: { productForm: true, receive: true, adjust: true, newOrder: true, fulfil: true },
  sales: { productForm: false, receive: false, adjust: false, newOrder: true, fulfil: false },
  warehouse: { productForm: false, receive: true, adjust: true, newOrder: false, fulfil: true },
}

async function regionCount(browser: Browser, role: Role, path: string, region: string) {
  const { context, page } = await openAs(browser, role, path)
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(page.getByRole('main').getByText('Loading', { exact: false })).toHaveCount(0)
  const count = await page.getByRole('region', { name: region, exact: true }).count()
  await context.close()
  return count
}

for (const role of ['admin', 'sales', 'warehouse'] as const) {
  test(`${role}: the actions on each page match the role`, async ({ browser, adminApi }) => {
    const expected = VISIBLE[role]
    // One reserved order so that the fulfillment queue has a row to act on.
    await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
    await adminApi.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 10 })
    await adminApi.post('/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 2 }] })
    await adminApi.post('/api/orders/1/submit')

    expect(await regionCount(browser, role, '/products', 'Add product') > 0).toBe(expected.productForm)
    expect(await regionCount(browser, role, '/inventory', 'Receive stock') > 0).toBe(expected.receive)
    expect(await regionCount(browser, role, '/inventory', 'Adjust stock') > 0).toBe(expected.adjust)
    expect(await regionCount(browser, role, '/orders', 'New order') > 0).toBe(expected.newOrder)

    const { context, page } = await openAs(browser, role, '/fulfillment')
    const row = page.getByRole('row', { name: /#1/ })
    await expect(row).toBeVisible() // everyone may look at the queue
    await expect(row.getByRole('button', { name: 'Pick' })).toHaveCount(expected.fulfil ? 1 : 0)
    if (!expected.fulfil) {
      await expect(page.getByText('Only warehouse staff and admins can pick, pack and ship. You can view the queue.')).toBeVisible()
    }
    await context.close()
  })
}

test('only an Admin sees the product catalog as editable', async ({ browser, adminApi }) => {
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })

  const sales = await openAs(browser, 'sales', '/products')
  await expect(sales.page.getByRole('row', { name: /LAP-1/ })).toBeVisible() // may look
  await expect(sales.page.getByText('Only an Admin can change products. You can view the catalog.')).toBeVisible()
  await expect(sales.page.getByRole('button', { name: /edit|deactivate/i })).toHaveCount(0)
  await sales.context.close()

  const admin = await openAs(browser, 'admin', '/products')
  await expect(admin.page.getByRole('button', { name: /edit/i }).first()).toBeVisible()
  await admin.context.close()
})

// ---- HTTP 401 and 403, straight at the API ----

interface Endpoint {
  label: string
  method: 'get' | 'post' | 'patch'
  path: string
  /** Roles that may call it; everyone else signed in gets 403. */
  allowed: Role[]
}

const ALL: Role[] = ['admin', 'sales', 'warehouse']
const ENDPOINTS: Endpoint[] = [
  { label: 'list products', method: 'get', path: '/api/products', allowed: ALL },
  { label: 'dashboard', method: 'get', path: '/api/dashboard/summary', allowed: ALL },
  { label: 'list orders', method: 'get', path: '/api/orders', allowed: ALL },
  { label: 'inventory', method: 'get', path: '/api/inventory?warehouseId=1', allowed: ALL },
  { label: 'create product', method: 'post', path: '/api/products', allowed: ['admin'] },
  { label: 'update product', method: 'patch', path: '/api/products/1', allowed: ['admin'] },
  { label: 'receive stock', method: 'post', path: '/api/inventory/receipts', allowed: ['admin', 'warehouse'] },
  { label: 'adjust stock', method: 'post', path: '/api/inventory/adjustments', allowed: ['admin', 'warehouse'] },
  { label: 'create order', method: 'post', path: '/api/orders', allowed: ['admin', 'sales'] },
  { label: 'submit order', method: 'post', path: '/api/orders/1/submit', allowed: ['admin', 'sales'] },
  { label: 'cancel order', method: 'post', path: '/api/orders/1/cancel', allowed: ['admin', 'sales'] },
  { label: 'pick order', method: 'post', path: '/api/orders/1/pick', allowed: ['admin', 'warehouse'] },
  { label: 'pack order', method: 'post', path: '/api/orders/1/pack', allowed: ['admin', 'warehouse'] },
  { label: 'ship order', method: 'post', path: '/api/orders/1/ship', allowed: ['admin', 'warehouse'] },
]

test('anonymous requests to every protected endpoint get 401, with a problem+json body', async ({ playwright }) => {
  const { api, dispose } = await anonymousApi(playwright)

  for (const endpoint of ENDPOINTS) {
    const response = endpoint.method === 'get' ? await api.get(endpoint.path) : await api.send(endpoint.method, endpoint.path, {})
    expect(response.status(), endpoint.label).toBe(401)
    expect(response.headers()['content-type'], endpoint.label).toContain('application/problem+json')
  }
  await dispose()
})

test('the health check, login and the CSRF token are the only open endpoints', async ({ playwright }) => {
  const { api, dispose } = await anonymousApi(playwright)

  expect((await api.get('/health')).status()).toBe(200)
  expect((await api.get('/api/auth/csrf')).status()).toBe(200)
  expect((await api.get('/api/auth/me')).status()).toBe(401)
  expect((await api.post('/api/auth/logout')).status()).toBe(401)
  await dispose()
})

for (const role of ['sales', 'warehouse'] as const) {
  test(`${role}: every action outside the role gets 403 and every allowed one gets through`, async ({ salesApi, warehouseApi }) => {
    const api = role === 'sales' ? salesApi : warehouseApi

    for (const endpoint of ENDPOINTS) {
      const response = endpoint.method === 'get' ? await api.get(endpoint.path) : await api.send(endpoint.method, endpoint.path, {})
      if (endpoint.allowed.includes(role)) {
        expect(response.status(), `${role} ${endpoint.label}`).not.toBe(403)
        expect(response.status(), `${role} ${endpoint.label}`).not.toBe(401)
      } else {
        expect(response.status(), `${role} ${endpoint.label}`).toBe(403)
        expect(response.headers()['content-type'], endpoint.label).toContain('application/problem+json')
      }
    }
  })
}

test('admin is never refused', async ({ adminApi }) => {
  for (const endpoint of ENDPOINTS) {
    const response = endpoint.method === 'get' ? await adminApi.get(endpoint.path) : await adminApi.send(endpoint.method, endpoint.path, {})
    expect([401, 403], endpoint.label).not.toContain(response.status())
  }
})

test('a Sales browser that is made to show the Warehouse buttons is still refused by the server', async ({ browser }) => {
  const { context, page } = await openAs(browser, 'sales', '/')
  // Pretend the server told the screen this user may do everything. The screen believes it; the API does not.
  await page.route('**/api/auth/me', async (route) => {
    const response = await route.fetch()
    const body = await response.json()
    body.permissions = ['ManageProducts', 'ManageStock', 'ManageOrders', 'FulfillOrders']
    await route.fulfill({ response, json: body })
  })
  await page.reload()
  await page.getByRole('link', { name: 'Products' }).click()

  await page.getByLabel('SKU').fill('HACK-1')
  await page.getByLabel('Name').fill('Should not exist')
  await page.getByLabel('Price').fill('1')
  await page.getByRole('button', { name: 'Add product' }).click()

  await expect(page.getByRole('alert')).toHaveText('Your role does not allow this action.')
  await expect(page).toHaveURL(/\/products$/) // 403 means "signed in, not allowed": the session stays
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()
  await context.close()
})
