import type { Page } from '@playwright/test'
import { sessionFile, type ApiClient } from './support/accounts.ts'
import { CUSTOMER, WAREHOUSE, resetDatabase } from './support/database.ts'
import { expect, test } from './support/fixtures.ts'

// Real browser, real React app, real ASP.NET Core API, real PostgreSQL (the isolated "techstore_e2e" database).
// The React app only sends requests and shows answers; every number checked below was calculated by the backend.
// These tests run as the Admin (who may do everything); the role-specific journeys are in roles-workflow.spec.ts.

test.use({ storageState: sessionFile('admin') })

test.beforeEach(() => {
  resetDatabase() // known starting point: two warehouses, two customers, nothing else
})

const LAPTOP = 'LAP-1 — Laptop Pro 14'

/** Creates the laptop product through the API (the full journey test does it through the UI instead). */
async function createLaptopViaApi(api: ApiClient) {
  const response = await api.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  expect(response.status()).toBe(201)
}

async function receiveViaApi(api: ApiClient, quantity: number) {
  const response = await api.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity })
  expect(response.status()).toBe(201)
}

/** Fills the "New order" form for the laptop in the Dallas warehouse and creates the draft. */
async function createDraftOrder(page: Page, quantity: string) {
  await page.goto('/orders')
  const form = page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: CUSTOMER })
  await form.getByLabel('Warehouse').selectOption({ label: WAREHOUSE })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill(quantity)
  await form.getByRole('button', { name: 'Create draft order' }).click()
  await expect(page.getByRole('region', { name: 'Order #1' })).toBeVisible()
}

/** The number in the "Stock in Dallas" panel on the Orders page, for example the label "Available". */
const stockPanelNumber = (page: Page, label: string) =>
  page.getByRole('region', { name: `Stock in ${WAREHOUSE}` }).locator(`dt:text-is("${label}") + dd`)

test('five pages can be reached from the navigation', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()

  for (const name of ['Products', 'Inventory', 'Orders', 'Fulfillment', 'Dashboard']) {
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name }).click()
    await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  }
})

test('receive 10 laptops, order 2, reserve, pick, pack, ship, and see 8 available (all through the browser)', async ({ page }) => {
  // 1. Add the product.
  await page.goto('/products')
  await page.getByLabel('SKU').fill('LAP-1')
  await page.getByLabel('Name').fill('Laptop Pro 14')
  await page.getByLabel('Price').fill('1299.99')
  await page.getByRole('button', { name: 'Add product' }).click()
  await expect(page.getByText('LAP-1 added.')).toBeVisible()
  await expect(page.getByRole('row', { name: /LAP-1.*Laptop Pro 14.*\$1,299\.99.*Active/ })).toBeVisible()

  // 2. Receive 10 laptops into Dallas.
  await page.goto('/inventory')
  await expect(page.getByLabel('Warehouse')).toHaveValue('1')
  const receive = page.getByRole('region', { name: 'Receive stock' })
  await receive.getByLabel('Product').selectOption({ label: LAPTOP })
  await receive.getByLabel('Quantity').fill('10')
  await receive.getByLabel('Note').fill('Supplier delivery')
  await receive.getByRole('button', { name: 'Receive stock' }).click()
  await expect(page.getByText(/Received 10 × LAP-1\. On hand is now 10\./)).toBeVisible()
  const stockRow = page.getByRole('row', { name: /LAP-1/ })
  await expect(stockRow.getByRole('cell')).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10'])

  // 3. Draft an order for 2 laptops. Creating a draft reserves nothing.
  await createDraftOrder(page, '2')
  const order = page.getByRole('region', { name: 'Order #1' })
  await expect(order.getByText('Draft', { exact: true })).toBeVisible()
  await expect(stockPanelNumber(page, 'Available')).toHaveText('10')

  // 4. Submit: 2 units are reserved, 8 remain available.
  await order.getByRole('button', { name: 'Submit order' }).click()
  await expect(order.getByText('Reserved', { exact: true })).toBeVisible()
  await expect(stockPanelNumber(page, 'Reserved')).toHaveText('2')
  await expect(stockPanelNumber(page, 'Available')).toHaveText('8')

  // 5. Pick, pack and ship.
  await page.goto('/fulfillment')
  const row = page.getByRole('row', { name: /#1/ })
  await row.getByRole('button', { name: 'Pick' }).click()
  await expect(row.getByText('Picking', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Pack' }).click()
  await expect(row.getByText('Packed', { exact: true })).toBeVisible()
  await row.getByRole('button', { name: 'Ship' }).click()
  await expect(page.getByText('Order #1 is now shipped.')).toBeVisible()
  await expect(row).toHaveCount(0) // a shipped order leaves the queue
  await expect(page.getByText('Nothing to pick, pack or ship right now.')).toBeVisible()
  await page.getByLabel('Show').selectOption({ label: 'Shipped' })
  await expect(page.getByRole('row', { name: /#1.*Shipped/ })).toBeVisible()

  // 6. Stock now: 8 on hand, nothing reserved, 8 available.
  await page.goto('/inventory')
  await expect(page.getByRole('row', { name: /LAP-1/ }).getByRole('cell')).toHaveText(['LAP-1', 'Laptop Pro 14', '8', '0', '8'])

  // 7. The dashboard agrees.
  await page.goto('/')
  await expect(page.locator('.stat', { hasText: 'Units available' }).locator('.stat__value')).toHaveText('8')
  await expect(page.locator('.stat', { hasText: 'Shipped orders' }).locator('.stat__value')).toHaveText('1')
})

test('ordering 15 when only 10 are available shows an understandable error and changes no stock', async ({ page, adminApi }) => {
  await createLaptopViaApi(adminApi)
  await receiveViaApi(adminApi, 10)

  await createDraftOrder(page, '15')
  const order = page.getByRole('region', { name: 'Order #1' })
  await order.getByRole('button', { name: 'Submit order' }).click()

  await expect(page.getByRole('alert')).toHaveText('Insufficient stock for SKU LAP-1: requested 15, available 10.')
  await expect(order.getByText('Draft', { exact: true })).toBeVisible()
  await expect(order.getByRole('button', { name: 'Submit order' })).toBeEnabled()
  await expect(stockPanelNumber(page, 'Reserved')).toHaveText('0')
  await expect(stockPanelNumber(page, 'Available')).toHaveText('10')

  // The stock is unchanged on the Inventory page too.
  await page.goto('/inventory')
  await expect(page.getByRole('row', { name: /LAP-1/ }).getByRole('cell')).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '0', '10'])
})

test('canceling a reserved order gives the stock back', async ({ page, adminApi }) => {
  await createLaptopViaApi(adminApi)
  await receiveViaApi(adminApi, 10)

  await createDraftOrder(page, '4')
  const order = page.getByRole('region', { name: 'Order #1' })
  await order.getByRole('button', { name: 'Submit order' }).click()
  await expect(stockPanelNumber(page, 'Available')).toHaveText('6')

  await order.getByRole('button', { name: 'Cancel order' }).click()

  await expect(order.getByText('Canceled', { exact: true })).toBeVisible()
  await expect(stockPanelNumber(page, 'Available')).toHaveText('10')
  await expect(order.getByRole('button', { name: 'Cancel order' })).toHaveCount(0)
})

test('adding a product with a SKU that already exists shows the server message', async ({ page, adminApi }) => {
  await createLaptopViaApi(adminApi)

  await page.goto('/products')
  await page.getByLabel('SKU').fill('lap-1')
  await page.getByLabel('Name').fill('Another laptop')
  await page.getByLabel('Price').fill('900')
  await page.getByRole('button', { name: 'Add product' }).click()

  await expect(page.getByRole('alert')).toHaveText("A product with SKU 'LAP-1' already exists.")
  await expect(page.getByLabel('Name')).toHaveValue('Another laptop') // what was typed is kept
})

test('an adjustment below the reserved quantity is refused with the server message', async ({ page, adminApi }) => {
  await createLaptopViaApi(adminApi)
  await receiveViaApi(adminApi, 10)
  await createDraftOrder(page, '4')
  await page.getByRole('region', { name: 'Order #1' }).getByRole('button', { name: 'Submit order' }).click()
  await expect(stockPanelNumber(page, 'Reserved')).toHaveText('4')

  await page.goto('/inventory')
  const adjust = page.getByRole('region', { name: 'Adjust stock' })
  await adjust.getByLabel('Product').selectOption({ label: LAPTOP })
  await adjust.getByLabel('Change').fill('-7')
  await adjust.getByLabel('Reason').fill('Recount')
  await adjust.getByRole('button', { name: 'Adjust stock' }).click()

  await expect(page.getByRole('alert')).toHaveText('The adjustment would reduce on-hand stock below the quantity already reserved.')
  await expect(page.getByRole('row', { name: /LAP-1/ }).getByRole('cell')).toHaveText(['LAP-1', 'Laptop Pro 14', '10', '4', '6'])
})
