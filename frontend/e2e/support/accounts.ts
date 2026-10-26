// The test accounts, signing in through the real login screen, and a small client for calling the API as a signed-in account.
import { expect, type APIRequestContext, type Browser, type BrowserContext, type Page, type PlaywrightWorkerArgs } from '@playwright/test'
import config from './e2e.config.json' with { type: 'json' }

export type Playwright = PlaywrightWorkerArgs['playwright']

export type AccountKey = 'admin' | 'sales' | 'warehouse' | 'logout' | 'revoke' | 'lockout' | 'expiry' | 'ratelimit'

export const webUrl = `http://127.0.0.1:${config.webPort}`
export const strictWebUrl = `http://127.0.0.1:${config.strictWebPort}`

export const emailOf = (key: AccountKey) => config.accounts.find((account) => account.key === key)!.email

/** The password is random for each run (see playwright.config.ts). */
export function passwordOfTestAccounts() {
  const password = process.env.E2E_PASSWORD
  if (!password) throw new Error('E2E_PASSWORD is not set')
  return password
}

/** Where the saved browser session of a shared account is kept (test-results is cleaned at the start of each run and git-ignored). */
export const sessionFile = (key: AccountKey) => `test-results/sessions/${key}.json`

/** Types the credentials into the login screen and submits. Does not wait for the result. */
export async function submitLogin(page: Page, email: string, password: string) {
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

/** Signs in through the login screen and waits until the dashboard is showing. */
export async function signInThroughUi(page: Page, key: AccountKey, baseUrl = webUrl) {
  await page.goto(`${baseUrl}/login`)
  await submitLogin(page, emailOf(key), passwordOfTestAccounts())
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible()
}

/** A new browser context (like another computer) that already holds the saved session of a shared account. */
export async function newSessionContext(browser: Browser, key: AccountKey): Promise<BrowserContext> {
  return browser.newContext({ baseURL: webUrl, storageState: sessionFile(key) })
}

/** Opens a page for an account in a brand-new browser context. */
export async function openAs(browser: Browser, key: AccountKey, path: string) {
  const context = await newSessionContext(browser, key)
  const page = await context.newPage()
  await page.goto(path)
  return { context, page }
}

/**
 * Calls the API with the cookies of a context (the signed-in session, or none). Unsafe requests carry the CSRF token the
 * server hands out for the current identity, exactly as the React app does.
 */
export class ApiClient {
  readonly http: APIRequestContext

  constructor(http: APIRequestContext) {
    this.http = http
  }

  get(path: string) {
    return this.http.get(path)
  }

  async csrfToken(): Promise<string> {
    const response = await this.http.get('/api/auth/csrf')
    expect(response.status()).toBe(200)
    return ((await response.json()) as { token: string }).token
  }

  async send(method: 'post' | 'put' | 'patch' | 'delete', path: string, data?: unknown, options: { csrf?: boolean } = {}) {
    const headers: Record<string, string> = options.csrf === false ? {} : { 'X-CSRF-TOKEN': await this.csrfToken() }
    return this.http.fetch(path, { method, headers, data })
  }

  post(path: string, data?: unknown) {
    return this.send('post', path, data)
  }
}

/** An API client with the saved session of a shared account. */
export async function apiAs(playwright: Playwright, key: AccountKey) {
  const http = await playwright.request.newContext({ baseURL: webUrl, storageState: sessionFile(key) })
  return { api: new ApiClient(http), dispose: () => http.dispose() }
}

/** An API client with no session at all. */
export async function anonymousApi(playwright: Playwright) {
  const http = await playwright.request.newContext({ baseURL: webUrl })
  return { api: new ApiClient(http), dispose: () => http.dispose() }
}

/** An API client that signs in through the API itself (used for accounts that tests log out or lock). */
export async function signedInApiFor(playwright: Playwright, key: AccountKey) {
  const http = await playwright.request.newContext({ baseURL: webUrl })
  const api = new ApiClient(http)
  const response = await api.post('/api/auth/login', { email: emailOf(key), password: passwordOfTestAccounts() })
  expect(response.status()).toBe(200)
  return { api, http, dispose: () => http.dispose() }
}
