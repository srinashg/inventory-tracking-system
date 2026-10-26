import { join } from 'node:path'
import { expect, type Browser, type BrowserContext, type Page } from '@playwright/test'

// Settings handed over by tests/containers/compose-e2e.sh (the password is random for each run and is never written to a file).
export const settings = {
  password: process.env.COMPOSE_PASSWORD ?? '',
  stateDir: process.env.COMPOSE_STATE_DIR ?? '',
  emails: {
    admin: process.env.COMPOSE_ADMIN_EMAIL ?? '',
    sales: process.env.COMPOSE_SALES_EMAIL ?? '',
    warehouse: process.env.COMPOSE_WAREHOUSE_EMAIL ?? '',
  },
}

export type Role = keyof typeof settings.emails

export const stateFile = (name: string) => join(settings.stateDir, name)
export const sessionFile = (role: Role) => stateFile(`${role}.json`)

export async function signIn(page: Page, role: Role) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(settings.emails[role])
  await page.getByLabel('Password').fill(settings.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()
}

/** A new browser (its own cookies) that is signed in as the role, through the real login screen. */
export async function newSignedInBrowser(browser: Browser, role: Role): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext()
  const page = await context.newPage()
  await signIn(page, role)
  return { context, page }
}

/** A new browser that already holds a saved session (to prove sessions survive a restart). */
export async function newBrowserWithSession(browser: Browser, role: Role) {
  const context = await browser.newContext({ storageState: sessionFile(role) })
  return { context, page: await context.newPage() }
}

/** Calls the API like the React app does: with the session cookie, plus the CSRF token on changes. */
export async function apiCall(page: Page, method: 'get' | 'post', path: string, data?: unknown) {
  if (method === 'get') return page.request.get(path)
  const token = ((await (await page.request.get('/api/auth/csrf')).json()) as { token: string }).token
  return page.request.fetch(path, { method, headers: { 'X-CSRF-TOKEN': token }, data })
}
