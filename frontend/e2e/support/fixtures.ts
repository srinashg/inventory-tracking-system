import { test as base } from '@playwright/test'
import { ApiClient, sessionFile, webUrl, type AccountKey, type Playwright } from './accounts.ts'

/** `test` with a ready-made API client for each shared account, used to set up data quickly (the tests still drive the UI). */
export const test = base.extend<{ adminApi: ApiClient; salesApi: ApiClient; warehouseApi: ApiClient }>({
  adminApi: async ({ playwright }, provide) => provide(await clientFor(playwright, 'admin')),
  salesApi: async ({ playwright }, provide) => provide(await clientFor(playwright, 'sales')),
  warehouseApi: async ({ playwright }, provide) => provide(await clientFor(playwright, 'warehouse')),
})

async function clientFor(playwright: Playwright, key: AccountKey) {
  const http = await playwright.request.newContext({ baseURL: webUrl, storageState: sessionFile(key) })
  return new ApiClient(http)
}

export { expect } from '@playwright/test'
