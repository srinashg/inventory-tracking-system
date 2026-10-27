// Starts two ASP.NET Core API instances for the end-to-end tests, both in the "Testing" environment and both against
// their OWN database, "techstore_e2e" (never the development database):
//   - the normal instance (port apiPort) with generous sign-in rate limits, so the many logins of a test run are not throttled;
//   - the strict instance (port strictApiPort) with a 1-minute idle timeout and the DEFAULT rate limits, used only by the
//     session-expiry and rate-limit tests.
// Steps: recreate the database (so the test accounts always get this run's random password), build once, apply the EF Core migrations,
// then run both instances. Playwright starts and stops this script (see playwright.config.ts).
import { execFileSync, spawn } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const config = JSON.parse(readFileSync(join(here, 'e2e.config.json'), 'utf8'))
const repoRoot = resolve(here, '../../..')
const project = join(repoRoot, 'backend/controllers')

const password = process.env.E2E_PASSWORD
if (!password) {
  console.error('[e2e] E2E_PASSWORD is not set (playwright.config.ts generates it for each run).')
  process.exit(1)
}

// Use the .NET SDK installed in ~/.dotnet when there is one; otherwise rely on the PATH.
const dotnetHome = join(homedir(), '.dotnet')
const localDotnet = existsSync(join(dotnetHome, 'dotnet'))
const dotnet = localDotnet ? join(dotnetHome, 'dotnet') : 'dotnet'
const env = {
  ...process.env,
  ...(localDotnet ? { DOTNET_ROOT: dotnetHome, PATH: `${dotnetHome}:${join(dotnetHome, 'tools')}:${process.env.PATH}` } : {}),
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
}

const connection = `Host=${config.pgHost};Port=${config.pgPort};Database=${config.database};Username=${config.pgUser}`
const psql = (database, sql) =>
  execFileSync('psql', ['-h', config.pgHost, '-p', config.pgPort, '-U', config.pgUser, '-d', database, '-tAc', sql], { env, encoding: 'utf8' })

// Safety: only a database whose name ends in "_e2e" is ever dropped.
if (!/^[a-z0-9_]+_e2e$/.test(config.database)) {
  console.error(`[e2e] refusing to recreate "${config.database}": the name must end in _e2e`)
  process.exit(1)
}
psql('postgres', `DROP DATABASE IF EXISTS "${config.database}" WITH (FORCE)`)
psql('postgres', `CREATE DATABASE "${config.database}"`)
console.log(`[e2e] created database ${config.database}`)

// Build first: it also restores the NuGet packages, which `dotnet ef` does not do. (On a fresh checkout, with no obj/ folders yet,
// `dotnet ef database update` alone fails with NETSDK1004 "Assets file not found".)
console.log('[e2e] building the API')
execFileSync(dotnet, ['build', project, '--nologo', '-v', 'q'], { env, stdio: 'inherit' })

console.log('[e2e] applying migrations')
execFileSync(dotnet, ['ef', 'database', 'update'], { cwd: join(repoRoot, 'backend/database'), env: { ...env, TECHSTORE_DB: connection }, stdio: 'inherit' })

// Test accounts exist only because the environment is "Testing" (the API refuses them anywhere else).
const accountSettings = Object.fromEntries(
  config.accounts.flatMap((account, i) => [
    [`Auth__TestAccounts__${i}__Email`, account.email],
    [`Auth__TestAccounts__${i}__Password`, password],
    [`Auth__TestAccounts__${i}__Role`, account.role],
  ]),
)

const children = []
function startApi(label, port, settings) {
  console.log(`[e2e] starting ${label} API on port ${port}`)
  const child = spawn(dotnet, ['run', '--no-build', '--project', project, '--no-launch-profile', '--urls', `http://127.0.0.1:${port}`], {
    env: { ...env, ASPNETCORE_ENVIRONMENT: 'Testing', ConnectionStrings__TechStore: connection, ...accountSettings, ...settings },
    stdio: 'inherit',
  })
  child.on('exit', (code) => {
    stopAll()
    process.exit(code ?? 0)
  })
  children.push(child)
}

function stopAll() {
  for (const child of children) child.kill()
}

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    stopAll()
    process.exit(0)
  })
}

async function waitUntilHealthy(port) {
  for (let attempt = 0; attempt < 120; attempt++) {
    try {
      if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) return
    } catch {
      // not listening yet
    }
    await new Promise((resolveWait) => setTimeout(resolveWait, 500))
  }
  throw new Error(`The API on port ${port} did not become healthy`)
}

startApi('normal', config.apiPort, {
  Auth__RateLimit__LoginPerClientPerMinute: '100000',
  Auth__RateLimit__LoginGlobalPerMinute: '100000',
})
// The second instance starts after the first is up so the two never race to create the same test accounts.
await waitUntilHealthy(config.apiPort)
startApi('strict', config.strictApiPort, { Auth__SessionMinutes: '1' }) // rate limits stay at their defaults
