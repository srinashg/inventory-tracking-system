import { expect, test } from '@playwright/test'
import { emailOf, passwordOfTestAccounts, signInThroughUi, submitLogin } from './support/accounts.ts'

// Signing in and out through the real login screen, and the redirects that protect every page.
// Each test here starts with no session at all (an empty browser context).

test.use({ storageState: { cookies: [], origins: [] } })

const PAGES = [
  { path: '/', heading: 'Dashboard' },
  { path: '/products', heading: 'Products' },
  { path: '/inventory', heading: 'Inventory' },
  { path: '/orders', heading: 'Orders' },
  { path: '/fulfillment', heading: 'Fulfillment' },
]

for (const { path } of PAGES) {
  test(`a visitor who is not signed in is sent from ${path} to the login page`, async ({ page }) => {
    await page.goto(path)

    await expect(page).toHaveURL(/\/login$/)
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Main' })).toHaveCount(0) // no menu, no data
  })
}

test('after signing in the visitor lands on the page they asked for', async ({ page }) => {
  await page.goto('/orders')
  await expect(page).toHaveURL(/\/login$/)

  await submitLogin(page, emailOf('sales'), passwordOfTestAccounts())

  await expect(page).toHaveURL(/\/orders$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Main' }).getByText(emailOf('sales'))).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Main' }).getByText('Sales', { exact: true })).toBeVisible()
})

test('a wrong password is refused with one generic message and nothing is stored in the browser', async ({ page }) => {
  await page.goto('/login')

  await submitLogin(page, emailOf('sales'), 'definitely-not-the-password')

  await expect(page.getByRole('alert')).toHaveText('Invalid email or password.')
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByLabel('Password')).toHaveValue('') // a rejected password is cleared from the screen
  expect(await page.evaluate(() => localStorage.length + sessionStorage.length)).toBe(0)
})

test('an unknown email gets exactly the same message as a wrong password', async ({ page }) => {
  await page.goto('/login')

  await submitLogin(page, 'nobody@e2e.test', 'definitely-not-the-password')

  await expect(page.getByRole('alert')).toHaveText('Invalid email or password.')
})

test('blank fields are caught on the screen without asking the server', async ({ page }) => {
  await page.goto('/login')
  let loginRequests = 0
  page.on('request', (request) => request.url().endsWith('/api/auth/login') && loginRequests++)

  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page.getByText('Enter your email.')).toBeVisible()
  await expect(page.getByText('Enter your password.')).toBeVisible()
  expect(loginRequests).toBe(0)
})

test('five wrong passwords lock the account, even for the correct password', async ({ page }) => {
  await page.goto('/login')
  for (let attempt = 0; attempt < 5; attempt++) {
    await submitLogin(page, emailOf('lockout'), `wrong-password-${attempt}`)
    await expect(page.getByRole('alert')).toHaveText('Invalid email or password.')
  }

  await submitLogin(page, emailOf('lockout'), passwordOfTestAccounts())

  await expect(page.getByRole('alert')).toHaveText('Invalid email or password.') // same message: a lockout is not revealed
  await expect(page).toHaveURL(/\/login$/)
})

test('signing out returns to the login page and protected pages stay closed until the next sign-in', async ({ page }) => {
  await signInThroughUi(page, 'logout')
  await page.getByRole('link', { name: 'Products' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible()

  await page.getByRole('button', { name: 'Sign out' }).click()

  await expect(page).toHaveURL(/\/login$/)
  await page.goto('/products')
  await expect(page).toHaveURL(/\/login$/) // the cookie is gone
  await page.goBack()
  await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toHaveCount(0)

  await submitLogin(page, emailOf('logout'), passwordOfTestAccounts()) // signing in again works
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()
})
