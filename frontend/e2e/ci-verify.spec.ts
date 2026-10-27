import { expect, test } from '@playwright/test'

// TEMPORARY: deliberately failing browser test (signed in via the default session) to prove CI turns red and that only a
// screenshot is kept. Never merged.
test('ci verification: this browser test is supposed to fail', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'This heading does not exist' })).toBeVisible({ timeout: 3000 })
})
