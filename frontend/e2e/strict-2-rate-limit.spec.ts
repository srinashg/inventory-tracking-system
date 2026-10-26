import { expect, test } from '@playwright/test'
import { emailOf, passwordOfTestAccounts, submitLogin } from './support/accounts.ts'

// Runs against the "strict" API instance, which keeps the DEFAULT limit: 10 sign-in attempts per minute per client.
// (The normal instance allows many more so the rest of the suite is never throttled.)
// This file runs after the session-expiry test, which waits more than a minute, so the rate-limit window starts empty.

test('the 11th sign-in attempt within a minute is refused with 429, even with the correct password', async ({ page }) => {
  await page.goto('/login')

  for (let attempt = 1; attempt <= 10; attempt++) {
    await submitLogin(page, 'nobody@e2e.test', `wrong-password-${attempt}`)
    await expect(page.getByRole('alert')).toHaveText('Invalid email or password.') // answered, just refused
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeEnabled()
  }

  const refused = page.waitForResponse((response) => response.url().endsWith('/api/auth/login') && response.status() === 429)
  await submitLogin(page, emailOf('ratelimit'), passwordOfTestAccounts())
  const response = await refused

  expect(Number(response.headers()['retry-after'])).toBeGreaterThan(0)
  expect(Number(response.headers()['retry-after'])).toBeLessThanOrEqual(60)
  await expect(page.getByRole('alert')).toHaveText('Too many sign-in attempts. Please wait a minute and try again.')
  await expect(page).toHaveURL(/\/login$/) // not signed in
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeEnabled()
})
