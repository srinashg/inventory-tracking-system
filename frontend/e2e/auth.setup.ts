import { mkdirSync } from 'node:fs'
import { test as setup } from '@playwright/test'
import { sessionFile, signInThroughUi } from './support/accounts.ts'

// Signs the three shared accounts in once, through the real login screen, and saves each browser session. The tests
// reuse these sessions instead of logging in again. Accounts that tests log out or lock have their own, separate accounts.
mkdirSync('test-results/sessions', { recursive: true })

for (const key of ['admin', 'sales', 'warehouse'] as const) {
  setup(`sign in as ${key}`, async ({ page }) => {
    await signInThroughUi(page, key)
    await page.context().storageState({ path: sessionFile(key) })
  })
}
