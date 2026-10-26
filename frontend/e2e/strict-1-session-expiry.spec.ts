import { expect, test } from '@playwright/test'
import { emailOf, signInThroughUi, strictWebUrl } from './support/accounts.ts'

// Runs against the "strict" API instance, whose idle timeout is 1 minute (Auth:SessionMinutes=1) instead of 8 hours.
// The 12-hour absolute limit cannot be waited for in a browser; it is tested with a controllable clock in
// tests/TechStore.Api.Tests/SessionApiTests.cs.

test.use({ storageState: { cookies: [], origins: [] } })

test('a session that has been idle for longer than the timeout is ended, and the screen says so', async ({ page }) => {
  await signInThroughUi(page, 'expiry', strictWebUrl)
  await expect(page.getByRole('navigation', { name: 'Main' }).getByText(emailOf('expiry'))).toBeVisible()
  expect((await page.request.get('/api/auth/me')).status()).toBe(200) // fine while the session is fresh

  // Do nothing for longer than the 1-minute idle timeout.
  await page.waitForTimeout(65_000)

  // The next thing the user does (opening another page) is refused by the server with 401 ...
  await page.getByRole('link', { name: 'Products' }).click()

  // ... and the screen sends them back to the login page with an explanation.
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByText('Your session has ended. Please sign in again.')).toBeVisible()
  expect((await page.request.get('/api/auth/me')).status()).toBe(401)

  // (It deliberately does not sign in again: that attempt would count towards the rate-limit window used by the next file.)
})
