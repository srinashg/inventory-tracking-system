import { randomBytes } from 'node:crypto'
import { defineConfig, devices } from '@playwright/test'
import config from './e2e/support/e2e.config.json' with { type: 'json' }

// A fresh random password for every test account on every run. It is passed to the API (which creates the accounts) and to
// the tests through the environment, and never written to a file. Workers inherit it because they start after this line.
process.env.E2E_PASSWORD ??= randomBytes(18).toString('base64url')

const webUrl = `http://127.0.0.1:${config.webPort}`
const strictWebUrl = `http://127.0.0.1:${config.strictWebPort}`

// End-to-end tests drive a real browser against the real stack, with authentication switched on:
//   browser -> Vite dev server -> ASP.NET Core API (Testing environment, test accounts) -> PostgreSQL database "techstore_e2e".
// Two API/Vite pairs run side by side (see e2e/support/start-api.mjs): the normal one, and a "strict" one with a 1-minute
// idle timeout and the default sign-in rate limits. Servers are never reused, so these tests cannot touch development data.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1, // every test resets the one shared test database
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  use: { trace: 'retain-on-failure' },
  projects: [
    // Signs each shared test account in once through the login screen and saves the browser sessions for the other tests.
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { ...devices['Desktop Chrome'], baseURL: webUrl } },
    {
      name: 'chromium',
      testIgnore: [/auth\.setup\.ts/, /strict-.*\.spec\.ts/],
      dependencies: ['setup'],
      use: { ...devices['Desktop Chrome'], baseURL: webUrl },
    },
    {
      name: 'strict',
      testMatch: /strict-.*\.spec\.ts/,
      dependencies: ['chromium'], // runs last: it needs more than a minute of real time
      timeout: 120_000,
      use: { ...devices['Desktop Chrome'], baseURL: strictWebUrl },
    },
  ],
  webServer: [
    {
      command: 'node e2e/support/start-api.mjs',
      url: `http://127.0.0.1:${config.strictApiPort}/health`, // the strict instance starts last
      reuseExistingServer: false,
      timeout: 300_000, // the first run compiles the backend
      stdout: 'ignore', // the API's own log is noisy; failures still show test output and traces
    },
    {
      command: `npx vite --host 127.0.0.1 --port ${config.webPort} --strictPort`,
      url: webUrl,
      reuseExistingServer: false,
      timeout: 60_000,
      env: { VITE_API_TARGET: `http://127.0.0.1:${config.apiPort}` },
    },
    {
      command: `npx vite --host 127.0.0.1 --port ${config.strictWebPort} --strictPort`,
      url: strictWebUrl,
      reuseExistingServer: false,
      timeout: 60_000,
      env: { VITE_API_TARGET: `http://127.0.0.1:${config.strictApiPort}` },
    },
  ],
})
