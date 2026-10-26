import { readFileSync } from 'node:fs'
import { expect, test } from '@playwright/test'
import { apiCall, newBrowserWithSession, stateFile } from './support.ts'

// Phase 2: the containers were stopped, removed and started again (volumes kept). The sessions saved in phase 1 must still work
// (the Data Protection keys survived), and the data must still be there (PostgreSQL volume), without anyone signing in again.

const SKU = () => readFileSync(stateFile('sku.txt'), 'utf8').trim()

test('the saved Admin session still works and needs no new login', async ({ browser }) => {
  const admin = await newBrowserWithSession(browser, 'admin')
  await admin.page.goto('/orders')
  await expect(admin.page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible()
  await expect(admin.page).not.toHaveURL(/\/login/)
  await admin.context.close()
})

test('the records survived: stock, orders and products are as they were', async ({ browser }) => {
  const warehouse = await newBrowserWithSession(browser, 'warehouse')
  await warehouse.page.goto('/inventory')
  await expect(warehouse.page.getByRole('row', { name: new RegExp(SKU()) }).getByRole('cell')).toHaveText([SKU(), 'Laptop Pro 14', '8', '0', '8'])
  await warehouse.context.close()

  const sales = await newBrowserWithSession(browser, 'sales')
  await sales.page.goto('/orders')
  const all = sales.page.getByRole('region', { name: 'All orders' })
  await expect(all.getByRole('row', { name: /#1.*Shipped/ })).toBeVisible()
  await expect(all.getByRole('row', { name: /#2.*Draft/ })).toBeVisible()
  await sales.context.close()
})

test('signing out still ends only that user’s sessions, and the stack keeps enforcing roles', async ({ browser }) => {
  const sales = await newBrowserWithSession(browser, 'sales')
  await sales.page.goto('/')
  await sales.page.getByRole('button', { name: 'Sign out' }).click()
  await expect(sales.page).toHaveURL(/\/login$/)
  expect((await apiCall(sales.page, 'get', '/api/orders')).status()).toBe(401)
  await sales.context.close()

  const warehouse = await newBrowserWithSession(browser, 'warehouse') // a different user: untouched
  await warehouse.page.goto('/')
  expect((await apiCall(warehouse.page, 'get', '/api/orders')).status()).toBe(200)
  expect((await apiCall(warehouse.page, 'post', '/api/orders', { customerId: 1, warehouseId: 1, items: [{ productId: 1, quantity: 1 }] })).status()).toBe(403)
  await warehouse.context.close()
})
