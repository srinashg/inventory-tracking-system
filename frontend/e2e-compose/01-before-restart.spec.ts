import { writeFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { apiCall, newSignedInBrowser, sessionFile, stateFile } from './support.ts'

// Phase 1 (fresh stack): every role signs in through the real UI and only sees and does what it is allowed to; the full
// receive -> order -> reserve -> pick -> pack -> ship workflow runs through the browser; an impossible order changes nothing.
// The sessions are saved so phase 2 can prove they survive the containers being recreated.

const SKU = `FLOW-${Date.now()}`
const LAPTOP = `${SKU} — Laptop Pro 14`
const stockRow = (page: Page) => page.getByRole('row', { name: new RegExp(SKU) }).getByRole('cell')
const stockNumber = (page: Page, label: string) => page.getByRole('region', { name: 'Stock in Dallas' }).locator(`dt:text-is("${label}") + dd`)

test.describe.configure({ mode: 'serial' })

test('Admin signs in, sees the whole menu and adds the product', async ({ browser }) => {
  const admin = await newSignedInBrowser(browser, 'admin')
  const nav = admin.page.getByRole('navigation', { name: 'Main' })
  await expect(nav.getByText('Administrator')).toBeVisible() // the display name the first Admin was given
  await expect(nav.getByText('Admin', { exact: true })).toBeVisible()

  await admin.page.getByRole('link', { name: 'Products' }).click()
  await admin.page.getByLabel('SKU').fill(SKU)
  await admin.page.getByLabel('Name').fill('Laptop Pro 14')
  await admin.page.getByLabel('Price').fill('1299.99')
  await admin.page.getByRole('button', { name: 'Add product' }).click()
  await expect(admin.page.getByText(`${SKU} added.`)).toBeVisible()

  // Admin may use every action.
  await admin.page.goto('/inventory')
  await expect(admin.page.getByRole('region', { name: 'Receive stock' })).toBeVisible()
  await expect(admin.page.getByRole('region', { name: 'Adjust stock' })).toBeVisible()
  await admin.page.goto('/orders')
  await expect(admin.page.getByRole('region', { name: 'New order' })).toBeVisible()

  await admin.context.storageState({ path: sessionFile('admin') })
  writeFileSync(stateFile('sku.txt'), SKU)
  await admin.context.close()
})

test('Warehouse sees only warehouse actions and receives 10 laptops', async ({ browser }) => {
  const warehouse = await newSignedInBrowser(browser, 'warehouse')
  await warehouse.page.goto('/products')
  await expect(warehouse.page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible()
  await expect(warehouse.page.getByRole('region', { name: 'Add product' })).toHaveCount(0)
  await warehouse.page.goto('/orders')
  await expect(warehouse.page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible()
  await expect(warehouse.page.getByRole('region', { name: 'New order' })).toHaveCount(0)

  await warehouse.page.goto('/inventory')
  const receive = warehouse.page.getByRole('region', { name: 'Receive stock' })
  await receive.getByLabel('Product').selectOption({ label: LAPTOP })
  await receive.getByLabel('Quantity').fill('10')
  await receive.getByRole('button', { name: 'Receive stock' }).click()
  await expect(warehouse.page.getByText(/Received 10 × .*On hand is now 10\./)).toBeVisible()
  await expect(stockRow(warehouse.page)).toHaveText([SKU, 'Laptop Pro 14', '10', '0', '10'])

  await warehouse.context.storageState({ path: sessionFile('warehouse') })
  await warehouse.context.close()
})

test('Sales cannot touch stock, creates an order for 2 and reserves it: 8 left available', async ({ browser }) => {
  const sales = await newSignedInBrowser(browser, 'sales')
  await sales.page.goto('/inventory')
  await expect(sales.page.getByRole('heading', { level: 1, name: 'Inventory' })).toBeVisible()
  await expect(stockRow(sales.page)).toHaveText([SKU, 'Laptop Pro 14', '10', '0', '10']) // may look
  await expect(sales.page.getByRole('region', { name: 'Receive stock' })).toHaveCount(0)
  await expect(sales.page.getByRole('region', { name: 'Adjust stock' })).toHaveCount(0)

  await sales.page.goto('/orders')
  const form = sales.page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: 'Amelia Reed' })
  await form.getByLabel('Warehouse').selectOption({ label: 'Dallas' })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill('2')
  await form.getByRole('button', { name: 'Create draft order' }).click()
  const order = sales.page.getByRole('region', { name: 'Order #1' })
  await order.getByRole('button', { name: 'Submit order' }).click()
  await expect(order.getByText('Reserved', { exact: true })).toBeVisible()
  await expect(stockNumber(sales.page, 'Reserved')).toHaveText('2')
  await expect(stockNumber(sales.page, 'Available')).toHaveText('8')

  await sales.page.goto('/fulfillment')
  await expect(sales.page.getByRole('row', { name: /#1/ })).toBeVisible()
  await expect(sales.page.getByRole('button', { name: 'Pick' })).toHaveCount(0) // Sales may look but not fulfil

  await sales.context.storageState({ path: sessionFile('sales') })
  await sales.context.close()
})

test('Warehouse picks, packs and ships: 8 on hand, 8 available', async ({ browser }) => {
  const warehouse = await newSignedInBrowser(browser, 'warehouse')
  await warehouse.page.goto('/fulfillment')
  const row = warehouse.page.getByRole('row', { name: /#1/ })
  await row.getByRole('button', { name: 'Pick' }).click()
  await expect(row.getByText('Picking', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Pack' }).click()
  await expect(row.getByText('Packed', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Ship' }).click()
  await expect(warehouse.page.getByText('Order #1 is now shipped.')).toBeVisible()

  await warehouse.page.goto('/inventory')
  await expect(stockRow(warehouse.page)).toHaveText([SKU, 'Laptop Pro 14', '8', '0', '8'])
  await warehouse.context.storageState({ path: sessionFile('warehouse') })
  await warehouse.context.close()
})

test('an order for 15 when 8 are available is refused with a clear message and changes nothing', async ({ browser }) => {
  const sales = await newSignedInBrowser(browser, 'sales')
  await sales.page.goto('/orders')
  const form = sales.page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: 'Amelia Reed' })
  await form.getByLabel('Warehouse').selectOption({ label: 'Dallas' })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill('15')
  await form.getByRole('button', { name: 'Create draft order' }).click()
  const order = sales.page.getByRole('region', { name: 'Order #2' })
  await order.getByRole('button', { name: 'Submit order' }).click()

  await expect(sales.page.getByRole('alert')).toHaveText(`Insufficient stock for SKU ${SKU}: requested 15, available 8.`)
  await expect(order.getByText('Draft', { exact: true })).toBeVisible()
  await expect(stockNumber(sales.page, 'Reserved')).toHaveText('0')
  await expect(stockNumber(sales.page, 'Available')).toHaveText('8')

  await sales.context.storageState({ path: sessionFile('sales') })
  await sales.context.close()
})

test('the API refuses what a role may not do (401 / 403), whatever the screen shows', async ({ browser }) => {
  const anonymous = await browser.newContext()
  const anonymousPage = await anonymous.newPage()
  expect((await apiCall(anonymousPage, 'get', '/api/products')).status()).toBe(401)
  expect((await apiCall(anonymousPage, 'get', '/api/orders')).status()).toBe(401)
  await anonymous.close()

  const sales = await newSignedInBrowser(browser, 'sales')
  expect((await apiCall(sales.page, 'get', '/api/products')).status()).toBe(200)
  expect((await apiCall(sales.page, 'post', '/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 1 })).status()).toBe(403)
  expect((await apiCall(sales.page, 'post', '/api/inventory/adjustments', { productId: 1, warehouseId: 1, quantityChange: -1, reason: 'x' })).status()).toBe(403)
  expect((await apiCall(sales.page, 'post', '/api/products', { sku: 'HACK', name: 'x', price: 1 })).status()).toBe(403)
  expect((await apiCall(sales.page, 'post', '/api/orders/1/pick')).status()).toBe(403)
  await sales.context.close()

  const warehouse = await newSignedInBrowser(browser, 'warehouse')
  expect((await apiCall(warehouse.page, 'post', '/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 1 }] })).status()).toBe(403)
  expect((await apiCall(warehouse.page, 'post', '/api/orders/2/cancel')).status()).toBe(403)
  expect((await apiCall(warehouse.page, 'post', '/api/products', { sku: 'HACK', name: 'x', price: 1 })).status()).toBe(403)
  await warehouse.context.close()

  // Nothing above changed the stock.
  const admin = await newSignedInBrowser(browser, 'admin')
  const stock = (await (await apiCall(admin.page, 'get', '/api/inventory?warehouseId=1')).json()) as { items: { sku: string; onHand: number; available: number }[] }
  expect(stock.items.find((item) => item.sku === SKU)).toMatchObject({ onHand: 8, available: 8 })
  await admin.context.close()
})

test('refreshing a deep link in the container keeps the user signed in', async ({ browser }) => {
  const sales = await newSignedInBrowser(browser, 'sales')
  await sales.page.goto('/orders')
  await sales.page.reload()
  await expect(sales.page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible()
  await sales.page.goto('/nonexistent/page')
  await expect(sales.page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  await sales.context.close()
})
