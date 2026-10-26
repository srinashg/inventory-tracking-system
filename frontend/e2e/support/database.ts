// Gives each end-to-end test a known, empty database with a few warehouses and customers.
// Warehouses and customers have no create endpoints in the API, so they are inserted directly.
import { execFileSync } from 'node:child_process'
import config from './e2e.config.json' with { type: 'json' }

export const WAREHOUSE = 'Dallas'
export const OTHER_WAREHOUSE = 'Seattle'
export const CUSTOMER = 'Amelia Reed'

function psql(sql: string) {
  execFileSync('psql', ['-h', config.pgHost, '-p', config.pgPort, '-U', config.pgUser, '-d', config.database, '-v', 'ON_ERROR_STOP=1', '-c', sql], {
    stdio: 'pipe',
  })
}

/** Empties every table, then adds the sample warehouses and customers. */
export function resetDatabase() {
  psql(`
    TRUNCATE TABLE inventory_movements, order_items, orders, inventory_balances, products, warehouses, customers RESTART IDENTITY CASCADE;
    INSERT INTO warehouses (name, location) VALUES ('${WAREHOUSE}', 'Dallas, TX'), ('${OTHER_WAREHOUSE}', 'Seattle, WA');
    INSERT INTO customers (name, email) VALUES ('${CUSTOMER}', 'amelia@example.com'), ('Marcus Bell', 'marcus@example.com');
  `)
}
