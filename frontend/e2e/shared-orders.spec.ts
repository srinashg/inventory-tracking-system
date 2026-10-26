import type { Page } from '@playwright/test'
import { openAs } from './support/accounts.ts'
import { CUSTOMER, WAREHOUSE, resetDatabase } from './support/database.ts'
import { expect, test } from './support/fixtures.ts'

// Orders live on the server, so every signed-in employee sees the same orders. Separate browser contexts (like different
// people on different computers) hold only their own session cookie and empty storage, so anything one of them sees
// comes from the API and the database, never from something remembered in the browser.
// Person A is a Sales employee (creates and submits orders); person B is a Warehouse employee (picks, packs, ships).

test.beforeEach(() => {
  resetDatabase()
})

const LAPTOP = 'LAP-1 — Laptop Pro 14'

const orderRow = (page: Page, id: number) => page.getByRole('row', { name: new RegExp(`#${id}\\b`) })

test('orders created and changed by one person are visible to another browser session', async ({ browser, adminApi }) => {

  // Set up through the API: a product with 10 in stock, order #1 (reserved) and order #2 (draft).
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  await adminApi.post('/api/inventory/receipts', { productId: 1, warehouseId: 1, quantity: 10 })
  for (const quantity of [2, 1]) {
    await adminApi.post('/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity }] })
  }
  expect((await adminApi.post('/api/orders/1/submit')).status()).toBe(200)

  // Person A and person B open the app with nothing stored in their browsers.
  const a = await openAs(browser, 'sales', '/orders')
  const b = await openAs(browser, 'warehouse', '/orders')
  for (const person of [a, b]) {
    expect(await person.page.evaluate(() => localStorage.length)).toBe(0)
    const all = person.page.getByRole('region', { name: 'All orders' })
    await expect(all.getByRole('row', { name: /#2.*Draft/ })).toBeVisible()
    await expect(all.getByRole('row', { name: /#1.*Reserved/ })).toBeVisible()
  }

  // The fulfillment queue shows the reserved order to both, and leaves the draft out.
  await a.page.goto('/fulfillment')
  await b.page.goto('/fulfillment')
  for (const person of [a, b]) {
    await expect(orderRow(person.page, 1)).toBeVisible()
    await expect(orderRow(person.page, 2)).toHaveCount(0)
  }

  // B (Warehouse) picks the order; A (Sales) refreshes and sees B's work.
  await orderRow(b.page, 1).getByRole('button', { name: 'Pick' }).click()
  await expect(orderRow(b.page, 1).getByText('Picking', { exact: true })).toBeVisible()
  await a.page.getByRole('button', { name: 'Refresh' }).click()
  await expect(orderRow(a.page, 1).getByText('Picking', { exact: true })).toBeVisible()
  await expect(orderRow(a.page, 1).getByRole('button', { name: 'Pack' })).toHaveCount(0) // Sales may look but not act

  // B packs and ships it; A refreshes and the order has left the queue.
  await orderRow(b.page, 1).getByRole('button', { name: 'Pack' }).click()
  await expect(orderRow(b.page, 1).getByText('Packed', { exact: true })).toBeVisible()
  await orderRow(b.page, 1).getByRole('button', { name: 'Ship' }).click()
  await expect(b.page.getByText('Order #1 is now shipped.')).toBeVisible()
  await a.page.getByRole('button', { name: 'Refresh' }).click()
  await expect(orderRow(a.page, 1)).toHaveCount(0)

  // A (Sales) creates a new order and submits it through the screen; B (Warehouse) sees it appear without having created it.
  await a.page.goto('/orders')
  const form = a.page.getByRole('region', { name: 'New order' })
  await form.getByLabel('Customer').selectOption({ label: CUSTOMER })
  await form.getByLabel('Warehouse').selectOption({ label: WAREHOUSE })
  await form.getByLabel('Product').selectOption({ label: LAPTOP })
  await form.getByLabel('Quantity').fill('3')
  await form.getByRole('button', { name: 'Create draft order' }).click()
  await expect(a.page.getByRole('region', { name: 'Order #3' })).toBeVisible()
  await a.page.getByRole('region', { name: 'Order #3' }).getByRole('button', { name: 'Submit order' }).click()
  await expect(a.page.getByRole('region', { name: 'Order #3' }).getByText('Reserved', { exact: true })).toBeVisible()

  await b.page.goto('/fulfillment')
  await expect(orderRow(b.page, 3).getByText('Reserved', { exact: true })).toBeVisible()
  await b.page.goto('/orders')
  const bAll = b.page.getByRole('region', { name: 'All orders' })
  await expect(bAll.getByRole('row', { name: /#3.*Reserved/ })).toBeVisible()
  await expect(bAll.getByRole('row', { name: /#1.*Shipped/ })).toBeVisible()

  // Nothing was ever stored in either browser.
  expect(await a.page.evaluate(() => localStorage.length)).toBe(0)
  expect(await b.page.evaluate(() => localStorage.length)).toBe(0)

  await a.context.close()
  await b.context.close()
})

test('the Orders page filter and View show any order to a brand-new browser session', async ({ browser, adminApi }) => {
  await adminApi.post('/api/products', { sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
  await adminApi.post('/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 2 }] })
  await adminApi.post('/api/orders', { customerId: 2, warehouseId: 2, items: [{ productId: 1, quantity: 1 }] })
  await adminApi.post('/api/orders/1/cancel')

  const person = await openAs(browser, 'warehouse', '/orders')
  const all = person.page.getByRole('region', { name: 'All orders' })
  await expect(all.getByRole('row', { name: /#2/ })).toBeVisible()
  await expect(all.getByRole('row', { name: /#1/ })).toBeVisible()

  await all.getByLabel('Status').selectOption({ label: 'Canceled' })
  await expect(all.getByRole('row', { name: /#1.*Canceled/ })).toBeVisible()
  await expect(all.getByRole('row', { name: /#2/ })).toHaveCount(0)

  await all.getByRole('row', { name: /#1/ }).getByRole('button', { name: 'View' }).click()
  const detail = person.page.getByRole('region', { name: 'Order #1' })
  await expect(detail.getByText('Laptop Pro 14')).toBeVisible()
  await expect(detail.getByText(CUSTOMER)).toBeVisible()
  await expect(detail.getByText('Canceled', { exact: true })).toBeVisible()

  await person.context.close()
})
