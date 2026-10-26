import { ApiClient, anonymousApi, emailOf, passwordOfTestAccounts, signInThroughUi, webUrl } from './support/accounts.ts'
import { resetDatabase } from './support/database.ts'
import { expect, test } from './support/fixtures.ts'

// CSRF protection, the session cookie, and what signing out really does on the server.

test.beforeEach(() => {
  resetDatabase()
})

const PRODUCT = { sku: 'SEC-1', name: 'Security test item', price: 10 }

test.describe('CSRF protection', () => {
  test('a signed-in change without the CSRF token is refused, with the token it succeeds', async ({ adminApi }) => {
    const without = await adminApi.send('post', '/api/products', PRODUCT, { csrf: false })
    expect(without.status()).toBe(400)
    expect(without.headers()['content-type']).toContain('application/problem+json')

    const bogus = await adminApi.http.fetch('/api/products', { method: 'post', headers: { 'X-CSRF-TOKEN': 'not-a-real-token' }, data: PRODUCT })
    expect(bogus.status()).toBe(400)

    expect((await adminApi.get('/api/products')).status()).toBe(200)
    const products = (await (await adminApi.get('/api/products')).json()) as { items: unknown[] }
    expect(products.items).toHaveLength(0) // neither refused request created anything

    expect((await adminApi.post('/api/products', PRODUCT)).status()).toBe(201)
  })

  test('a token that belongs to another user is refused', async ({ adminApi, salesApi }) => {
    const salesToken = await salesApi.csrfToken()

    const response = await adminApi.http.fetch('/api/products', { method: 'post', headers: { 'X-CSRF-TOKEN': salesToken }, data: PRODUCT })

    expect(response.status()).toBe(400)
  })

  test('the token fetched before signing in does not work after signing in', async ({ playwright }) => {
    const http = await playwright.request.newContext({ baseURL: webUrl })
    const api = new ApiClient(http)
    const anonymousToken = await api.csrfToken()
    const login = await http.fetch('/api/auth/login', {
      method: 'post',
      headers: { 'X-CSRF-TOKEN': anonymousToken },
      data: { email: emailOf('admin'), password: passwordOfTestAccounts() },
    })
    expect(login.status()).toBe(200)

    const stale = await http.fetch('/api/products', { method: 'post', headers: { 'X-CSRF-TOKEN': anonymousToken }, data: PRODUCT })
    expect(stale.status()).toBe(400)
    expect((await api.post('/api/products', PRODUCT)).status()).toBe(201) // a token fetched after signing in works
    await http.dispose()
  })

  test('the screen fetches a fresh token after signing in, so an order action works straight after login', async ({ page }) => {
    await page.context().clearCookies()
    await signInThroughUi(page, 'admin')
    await page.getByRole('link', { name: 'Products' }).click()

    await page.getByLabel('SKU').fill('SEC-2')
    await page.getByLabel('Name').fill('Right after login')
    await page.getByLabel('Price').fill('5')
    await page.getByRole('button', { name: 'Add product' }).click()

    await expect(page.getByText('SEC-2 added.')).toBeVisible()
  })
})

test.describe('the session cookie', () => {
  test('is HttpOnly and SameSite=Strict, and neither the cookie nor any token is reachable from JavaScript or storage', async ({ page }) => {
    await page.context().clearCookies()
    await signInThroughUi(page, 'sales')

    const session = (await page.context().cookies()).find((cookie) => cookie.name === 'techstore.session')
    expect(session?.httpOnly).toBe(true)
    expect(session?.sameSite).toBe('Strict')
    expect(session?.path).toBe('/')

    expect(await page.evaluate(() => (globalThis as unknown as { document: { cookie: string } }).document.cookie)).not.toContain('techstore.session')
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }))
    expect(stored).toBe('{}{}')
    expect(stored).not.toContain(passwordOfTestAccounts())
  })

  test('API responses are not cacheable and carry the protective headers', async ({ adminApi }) => {
    const response = await adminApi.get('/api/products')

    const headers = response.headers()
    expect(headers['cache-control']).toBe('no-store')
    expect(headers['x-content-type-options']).toBe('nosniff')
    expect(headers['x-frame-options']).toBe('DENY')
    expect(headers['referrer-policy']).toBe('no-referrer')
    expect(headers['content-security-policy']).toContain("frame-ancestors 'none'")
  })
})

test.describe('session revocation after logout', () => {
  test('a copied session cookie stops working the moment the user signs out', async ({ browser, playwright }) => {
    const context = await browser.newContext({ baseURL: webUrl })
    const page = await context.newPage()
    await signInThroughUi(page, 'logout')

    // Someone copies the cookie (for example from a stolen backup of the browser profile).
    const copy = await playwright.request.newContext({ baseURL: webUrl, storageState: await context.storageState() })
    expect((await copy.get('/api/auth/me')).status()).toBe(200)

    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page).toHaveURL(/\/login$/)

    expect((await copy.get('/api/auth/me')).status()).toBe(401)
    expect((await copy.get('/api/products')).status()).toBe(401)
    await copy.dispose()
    await context.close()
  })

  test('signing out in one browser ends the same user’s session in every other browser', async ({ browser }) => {
    const a = await browser.newContext({ baseURL: webUrl })
    const b = await browser.newContext({ baseURL: webUrl })
    const pageA = await a.newPage()
    const pageB = await b.newPage()
    await signInThroughUi(pageA, 'logout')
    await signInThroughUi(pageB, 'logout')
    await pageB.getByRole('link', { name: 'Products' }).click()
    await expect(pageB.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible()

    await pageA.getByRole('button', { name: 'Sign out' }).click()
    await expect(pageA).toHaveURL(/\/login$/)

    // Browser B has not been touched, but its next request is refused and the screen sends it to the login page.
    await pageB.getByRole('link', { name: 'Orders' }).click()
    await expect(pageB).toHaveURL(/\/login$/)
    await expect(pageB.getByText('Your session has ended. Please sign in again.')).toBeVisible()
    await a.close()
    await b.close()
  })

  test('other users’ sessions stay valid', async ({ browser, salesApi }) => {
    const context = await browser.newContext({ baseURL: webUrl })
    const page = await context.newPage()
    await signInThroughUi(page, 'logout') // a Sales account, like the shared "sales" account
    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page).toHaveURL(/\/login$/)

    expect((await salesApi.get('/api/auth/me')).status()).toBe(200)
    expect((await salesApi.get('/api/orders')).status()).toBe(200)
    await context.close()
  })

  test('nobody can end a session without being signed in and holding a valid CSRF token', async ({ playwright }) => {
    const { api, dispose } = await anonymousApi(playwright)
    expect((await api.post('/api/auth/logout')).status()).toBe(401) // anonymous
    await dispose()

    const http = await playwright.request.newContext({ baseURL: webUrl })
    const victim = new ApiClient(http)
    expect((await victim.post('/api/auth/login', { email: emailOf('revoke'), password: passwordOfTestAccounts() })).status()).toBe(200)

    // A forged cross-site request has the cookie but not the token: it must not sign the user out.
    expect((await victim.send('post', '/api/auth/logout', undefined, { csrf: false })).status()).toBe(400)
    expect((await victim.get('/api/auth/me')).status()).toBe(200)

    expect((await victim.post('/api/auth/logout')).status()).toBe(204) // the genuine request works
    expect((await victim.get('/api/auth/me')).status()).toBe(401)
    await http.dispose()
  })
})
