import { defineConfig, devices } from '@playwright/test'

// Browser tests against the REAL Docker Compose stack (nginx web container -> API container -> PostgreSQL container).
// Unlike playwright.config.ts it starts nothing itself: tests/containers/compose-e2e.sh brings the stack up with throw-away secrets,
// creates the test accounts, sets COMPOSE_* variables and runs these tests before and after a full container restart.
export default defineConfig({
  testDir: './e2e-compose',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  outputDir: 'test-results/compose',
  use: { baseURL: process.env.COMPOSE_BASE_URL ?? 'http://127.0.0.1:8080', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
