import type { Page } from '@playwright/test'
import { openAs } from './support/accounts.ts'
import { CUSTOMER, WAREHOUSE, resetDatabase } from './support/database.ts'
import { expect, test } from './support/fixtures.ts'

// The inventory journey done by the people who really do it, each in their own browser session:
// Admin sets up the product, Warehouse receives stock, Sales takes the order, Warehouse fulfils it.

test.beforeEach(() => {
  resetDatabase()
})

const LAPTOP = 'LAP-1 — Laptop Pro 14'

const stockRow = (page: Page) => page.getByRole('row', { name: /LAP-1/ }).getByRole('cell')

test('Admin, Warehouse and Sales together: receive 10, order 2, reserve, pick, pack, ship, 8 available', async ({ browser }) => {
  const admin = await openAs(browser, 'admin', '/products')
  const warehouse = await openAs(browser, 'warehouse', '/inventory')
  const sales = await openAs(browser, 'sales', '/orders')

  // Admin adds the product.
  await admin.page.getByLabel('SKU').fill('LAP-1')
  await admin.page.getByLabel('Name').fill('Laptop Pro 14')
  await admin.page.getByLabel('Price').fill('1299.99')
  await admin.page.getByRole('button', { name: 'Add product' }).click()
  await expect(admin.page.getByText('LAP-1 added.')).toBeVisible()

  // Warehouse receives 10 laptops (the product list is loaded when the page opens, so reopen it).
  await warehouse.page.goto('/inventory')
  const receive = warehouse.page.getByRole('region', { name: 'Receive stock' })
  await receive.getByLabel('Product').selectOption({ label: LAPTOP })
  await receive.getByLabel('Quantity').fill('10')
  await receive.getByRole('button', { name: 'Receive stock' }).click()
  await expect(warehouse.page.getByText(/Received 10 × LAP-1\. On hand is now 10\./)).toBeVisible()
  await expect(stockRow(warehouse.page)).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10'])

  // Sales creates a draft for 2 and submits it: 2 reserved, 8 available.
  await sales.page.goto('/orders')
  const form = sales.page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: CUSTOMER })
  await form.getByLabel('Warehouse').selectOption({ label: WAREHOUSE })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill('2')
  await form.getByRole('button', { name: 'Create draft order' }).click()
  const order = sales.page.getByRole('region', { name: 'Order #1' })
  await order.getByRole('button', { name: 'Submit order' }).click()
  await expect(order.getByText('Reserved', { exact: true })).toBeVisible()
  const panel = sales.page.getByRole('region', { name: `Stock in ${WAREHOUSE}` })
  await expect(panel.locator('dt:text-is("Reserved") + dd')).toHaveText('2')
  await expect(panel.locator('dt:text-is("Available") + dd')).toHaveText('8')

  // Warehouse sees the new order and fulfils it.
  await warehouse.page.goto('/fulfillment')
  const row = warehouse.page.getByRole('row', { name: /#1/ })
  await row.getByRole('button', { name: 'Pick' }).click()
  await expect(row.getByText('Picking', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Pack' }).click()
  await expect(row.getByText('Packed', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Ship' }).click()
  await expect(warehouse.page.getByText('Order #1 is now shipped.')).toBeVisible()

  // Everybody agrees: 8 on hand, 8 available, the order is shipped.
  await warehouse.page.goto('/inventory')
  await expect(stockRow(warehouse.page)).toHaveText(['LAP-1', 'Laptop Pro 14', '8', '0', '8'])
  await sales.page.goto('/orders')
  await expect(sales.page.getByRole('region', { name: 'All orders' }).getByRole('row', { name: /#1.*Shipped/ })).toBeVisible()
  await admin.page.goto('/')
  await expect(admin.page.locator('.stat', { hasText: 'Units available' }).locator('.stat__value')).toHaveText('8')

  for (const person of [admin, warehouse, sales]) await person.context.close()
})

test('Sales cannot adjust stock: the screen offers no way, and the server refuses a forced attempt', async ({ browser, adminApi, salesApi }) => {
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  await adminApi.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 10 })

  const sales = await openAs(browser, 'sales', '/inventory')
  await expect(stockRow(sales.page)).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10']) // may look
  await expect(sales.page.getByRole('region', { name: 'Adjust stock' })).toHaveCount(0)
  await expect(sales.page.getByRole('region', { name: 'Receive stock' })).toHaveCount(0)

  const forced = await salesApi.post('/api/inventory/adjustments', { productId: 1, warehouseId: 1, quantityChange: -5, reason: 'sneaky' })
  expect(forced.status()).toBe(403)
  await sales.page.reload()
  await expect(stockRow(sales.page)).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10']) // nothing changed
  await sales.context.close()
})

test('Warehouse cannot create or cancel orders: no form on the screen, and the server refuses a forced attempt', async ({ browser, adminApi, warehouseApi }) => {
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  await adminApi.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 10 })
  await adminApi.post('/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 2 }] })
  await adminApi.post('/api/orders/1/submit')

  const warehouse = await openAs(browser, 'warehouse', '/orders')
  await expect(warehouse.page.getByRole('region', { name: 'New order' })).toHaveCount(0)
  await warehouse.page.getByLabel('Order number').fill('1')
  await warehouse.page.getByRole('button', { name: 'Open order' }).click()
  const order = warehouse.page.getByRole('region', { name: 'Order #1' })
  await expect(order.getByText('Reserved', { exact: true })).toBeVisible() // may look
  await expect(order.getByRole('button', { name: 'Cancel order' })).toHaveCount(0)

  expect((await warehouseApi.post('/api/orders/1/cancel')).status()).toBe(403)
  expect(
    (await warehouseApi.post('/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 1 }] })).status(),
  ).toBe(403)
  await warehouse.page.reload()
  await expect(warehouse.page.getByRole('region', { name: 'All orders' }).getByRole('row', { name: /#1.*Reserved/ })).toBeVisible()
  await warehouse.context.close()
})

test('Sales asking for 15 when 10 are available gets an understandable error and stock is unchanged', async ({ browser, adminApi }) => {
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  await adminApi.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 10 })

  const sales = await openAs(browser, 'sales', '/orders')
  const form = sales.page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: CUSTOMER })
  await form.getByLabel('Warehouse').selectOption({ label: WAREHOUSE })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill('15')
  await form.getByRole('button', { name: 'Create draft order' }).click()
  const order = sales.page.getByRole('region', { name: 'Order #1' })
  await order.getByRole('button', { name: 'Submit order' }).click()

  await expect(sales.page.getByRole('alert')).toHaveText('Insufficient stock for SKU LAP-1: requested 15, available 10.')
  await expect(order.getByText('Draft', { exact: true })).toBeVisible()
  await sales.page.goto('/inventory')
  await expect(stockRow(sales.page)).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10'])
  await sales.context.close()
})
