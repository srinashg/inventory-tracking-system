# TechStore Architecture: What the System Does

TechStore is an internal Inventory & Order Management tool for an electronics retailer. Sales and warehouse employees use a React app, which calls an ASP.NET Core API, which reads and writes PostgreSQL. Its main job is to keep **available stock accurate** so the same unit is never promised to two customers.

**Core inventory rule:** `Available = On hand − Reserved`

- Reserving stock lowers *available* only.
- Shipping lowers *on hand* (and releases the reservation).
- Available stock must never go negative.

**Request path for every action:** React UI → `Controller` → `Service` → `DbContext` → PostgreSQL → Response.

---

## 1. The Main Order Journey

Example: 2 laptops from the Dallas warehouse.

| # | Employee action | System response | Entities involved | Stock after (on hand / reserved / available) |
|---|---|---|---|---|
| 1 | **Receive** 10 laptops into Dallas | Inventory increases, movement logged | Product, Warehouse, InventoryBalance, InventoryMovement | 10 / 0 / 10 |
| 2 | **Draft** an order: customer, warehouse, laptop × 2 | Order saved as `Draft`, no stock change | Customer, Warehouse, Product, Order, OrderItem | 10 / 0 / 10 |
| 3 | **Submit** the order | Stock checked, 2 units reserved, status → `Reserved` | Order, OrderItem, InventoryBalance, InventoryMovement | 10 / 2 / 8 |
| 4 | **Pick**, then **Pack** | Status → `Picking`, then `Packed`; units stay reserved | Order | 10 / 2 / 8 |
| 5 | **Ship** | On hand and reserved both drop by 2, status → `Shipped` | Order, OrderItem, InventoryBalance, InventoryMovement | 8 / 0 / 8 |

Entity definitions are in `entities.md`.

## 2. Order Status Lifecycle

```
Draft ──submit──▶ Reserved ──pick──▶ Picking ──pack──▶ Packed ──ship──▶ Shipped
  │                  │
  └────cancel────────┴────cancel───▶ Canceled
```

- Any transition not shown is refused with a clear error and **changes nothing** (for example, shipping a `Draft`, or shipping an order twice).
- `Shipped` and `Canceled` are final.
- Cancellation is allowed only from `Draft` and `Reserved`. Cancelling an order that is already `Picking` or `Packed` is not supported in the first version (assumption based on the blueprint; to confirm).

---

## 3. User Requests and Expected Results

Each request lists what the user does, the API call behind it, the expected result, and how it fails. Failures never leave stock partly changed.

### 3.1 Products

| User request | API | Expected result | Failure cases |
|---|---|---|---|
| Browse products | `GET /api/products` | List of products (SKU, name, price, active flag) | none expected |
| Add a product | `POST /api/products` | Product saved; `201 Created` with its new ID | `400` missing/invalid SKU, name or price; `409` SKU already exists |
| Edit or deactivate a product | `PATCH /api/products/{id}` | Details updated, or product marked inactive | `404` unknown product; `400` invalid data; `409` SKU clashes with another product |

**Entities involved:** Product (write for add/edit/deactivate, read for list).

Rules: SKUs are unique. Deactivated products stay in the system for history but cannot be added to new orders (assumption; to confirm).

### 3.2 Reference Data (Warehouses and Customers)

| User request | API | Expected result | Failure cases |
|---|---|---|---|
| List warehouses (to pick one for an order or stock view) | `GET /api/warehouses` | List of warehouses | none expected |
| List customers (to pick one for an order) | `GET /api/customers` | List of customers (sample data in the first version) | none expected |

**Entities involved:** Warehouse (read), Customer (read).

### 3.3 Inventory

| User request | API | Expected result | Failure cases |
|---|---|---|---|
| View warehouse stock | `GET /api/inventory?warehouseId=1` | Per product: on hand, reserved, available | `404` unknown warehouse |
| Receive stock | `POST /api/inventory/receipts` | On hand increases; a stock-movement record is logged; updated balance returned | `400` quantity ≤ 0; `404` unknown product/warehouse |
| Correct stock (adjustment) | `POST /api/inventory/adjustments` | On hand changed by a signed amount (for example −2) with a required reason; movement logged | `400` missing reason; `404` unknown product/warehouse; `409` correction would push on hand below the reserved quantity |

**Entities involved:**
- View stock: InventoryBalance, Product, Warehouse (all read).
- Receive stock and adjust stock: InventoryBalance (write), InventoryMovement (write), Product and Warehouse (read, to confirm they exist).

Rules: one balance per product–warehouse pair (created on first receipt). Quantities are never negative. Every stock change is recorded as a movement for an audit trail.

### 3.4 Orders

| User request | API | Expected result | Failure cases |
|---|---|---|---|
| **Create a draft order** | `POST /api/orders` | Order saved as `Draft` with its line items and the price of each product *at that moment*; `201 Created` with order ID, status and total units. **No stock is reserved.** | `400` no items, quantity ≤ 0, duplicate product lines; `404` unknown customer/warehouse/product; `409` inactive product |
| **List orders** | `GET /api/orders` (optional `status`, repeatable, `customerId`, `warehouseId`, `page`, `pageSize`) | Every order, newest first, as summaries (no line items) with status and totals; for example `?status=Reserved&status=Picking&status=Packed` is the fulfillment queue | `400` unknown status or invalid paging/filter (no matches is an empty list, not an error) |
| **Open an order** | `GET /api/orders/{id}` | Order details, line items and current status | `404` unknown order |
| **Submit an order** | `POST /api/orders/{id}/submit` | In **one transaction**: every line is checked and reserved, status `Draft → Reserved`, updated order returned. Reserve **all lines or none**. | `404` unknown order; `409` order is not `Draft`; `409` insufficient stock on any line (nothing reserved, stock unchanged) |
| **Pick** | `POST /api/orders/{id}/pick` | Status `Reserved → Picking`; stock unchanged | `404`; `409` wrong status |
| **Pack** | `POST /api/orders/{id}/pack` | Status `Picking → Packed`; stock unchanged | `404`; `409` wrong status |
| **Ship** | `POST /api/orders/{id}/ship` | In **one transaction**: for each line on hand −= quantity and reserved −= quantity, movement logged, status `Packed → Shipped`. A repeated ship request cannot deduct twice. | `404`; `409` not `Packed` (including already `Shipped`) |
| **Cancel** | `POST /api/orders/{id}/cancel` | `Draft`: status → `Canceled`, no stock change. `Reserved`: reservation released (reserved decreases, available increases), status → `Canceled`. | `404`; `409` order is `Picking`, `Packed`, `Shipped` or already `Canceled` |

**Entities involved:**

| Request | Entities read | Entities written |
|---|---|---|
| Create draft | Customer, Warehouse, Product | Order, OrderItem |
| List orders | Order, OrderItem (for the totals) | none |
| Open order | Order, OrderItem, Customer, Warehouse, Product | none |
| Submit | Order, OrderItem | InventoryBalance (reserved ↑), InventoryMovement, Order (status) |
| Pick / Pack | Order | Order (status) |
| Ship | Order, OrderItem | InventoryBalance (on hand ↓, reserved ↓), InventoryMovement, Order (status) |
| Cancel | Order, OrderItem | Order (status); if `Reserved` also InventoryBalance (reserved ↓) and InventoryMovement |

**Example, insufficient stock:** 10 laptops are available and someone submits an order for 15. The API replies `409 Conflict` with a readable message. Nothing is reserved, the order stays `Draft`, and stock is unchanged.

### 3.5 Dashboard

| User request | API | Expected result | Failure cases |
|---|---|---|---|
| Open the dashboard | `GET /api/dashboard/summary` | Totals for available stock, low-stock items, open orders and shipped orders | none expected |

**Entities involved:** InventoryBalance, Product (available and low stock); Order, OrderItem (open and shipped orders). All read-only.

### 3.6 Access Control

| User request | Expected result |
|---|---|
| Sign in (`POST /api/auth/login`) | Valid email and password: `200`, a session cookie, and the user's roles and permissions. Wrong password, unknown email or locked account: the same `401`. There is no public registration. |
| Sign out (`POST /api/auth/logout`) | All of the user's sessions end, in every browser (a copied cookie stops working too); later requests return `401` until the user signs in again. Other users are unaffected. |
| Sign in too often (11th attempt in a minute from one client, or over the global cap) | `429 Too Many Requests` with `Retry-After`, even for a correct password. |
| Stay signed in | A session ends after 8 hours of inactivity, and after 12 hours in total however active the user is. |
| Ask who I am (`GET /api/auth/me`) | The signed-in user's name, role and permissions, which the React app uses to decide what to show. |
| Call any endpoint without being signed in | `401 Unauthorized`. Only `/health`, `GET /api/auth/csrf` and `POST /api/auth/login` are open. |
| Do something their role does not allow | `403 Forbidden`, enforced by the API even if the request did not come from the React app. |

| Role | May do |
|---|---|
| Admin | Everything |
| Sales | View everything; create, submit and cancel orders |
| Warehouse | View everything; receive and adjust stock; pick, pack and ship orders |

The endpoint-by-endpoint matrix is in `api-contract.md` §2.1; how sessions work and end is in §10.

### 3.7 Health Check

| User request | API | Expected result |
|---|---|---|
| Check the service is running | `GET /health` | Healthy response confirming the API is up and ready. |

---

## 4. Common Error Responses

| Status | Meaning in this system |
|---|---|
| `400 Bad Request` | Invalid input (missing fields, quantity ≤ 0, empty order) |
| `401 Unauthorized` | Not logged in (once security is added) |
| `403 Forbidden` | Logged in but role does not permit the action |
| `404 Not Found` | Order, product, customer or warehouse does not exist |
| `409 Conflict` | Valid request that the current state forbids: insufficient stock, invalid status transition, duplicate SKU |

---

## 5. End-to-End Scenarios the System Must Support

1. **Happy path:** Receive 10 laptops → draft an order for 2 → submit (reserve 2) → pick → pack → ship → 8 available.
2. **Insufficient stock:** Order 15 with 10 available → submission refused → stock and order unchanged.
3. **Cancel a reservation:** Cancel a `Reserved` order → reservation released → availability restored.
4. **Competing orders:** Two orders race for the last units → at most one succeeds; the other gets `409`. No overselling.
5. **Duplicate shipping:** Sending the ship request twice → the second is refused; stock is deducted only once.

---

## 6. The Employee Interface (React + TypeScript)

The app in `frontend/` only **sends requests and shows the server's answers**. Stock numbers, validation limits and order status transitions are decided by the backend; the UI shows clear loading, success, empty and error states and disables a button while its request is in flight.

**Flow:** user action in React → `api/client.ts` → Vite dev server (forwards `/api` to the API, so no CORS setup is needed) → Controller → Service → `DbContext` → PostgreSQL → response → React updates the screen.

| Page | What the user does | Endpoints used |
|---|---|---|
| Dashboard | Sees totals and low-stock alerts | `GET /api/dashboard/summary`, `GET /api/warehouses` |
| Products | Lists, adds, edits, deactivates and reactivates products | `GET/POST /api/products`, `PATCH /api/products/{id}` |
| Inventory | Picks a warehouse, sees stock, receives stock, adjusts stock | `GET /api/warehouses`, `GET /api/products`, `GET /api/inventory`, `POST /api/inventory/receipts`, `POST /api/inventory/adjustments` |
| Orders | Creates a draft, sees live stock, submits, cancels; browses all orders (filter by status, paged) and opens one | `GET /api/customers`, `/api/warehouses`, `/api/products`, `/api/inventory`, `POST /api/orders`, `GET /api/orders`, `GET /api/orders/{id}`, `POST /api/orders/{id}/submit`, `/cancel` |
| Fulfillment | Works the queue of Reserved, Picking and Packed orders (other statuses via the "Show" filter): picks, packs and ships | `GET /api/orders?status=Reserved&status=Picking&status=Packed`, `POST /api/orders/{id}/pick`, `/pack`, `/ship` |

**Signing in (Step 10D):** the app asks `GET /api/auth/me` when it loads. If the server answers `401`, every page redirects to `/login` and nothing is loaded; after signing in the user returns to the page they asked for. The session is an HttpOnly cookie managed by the browser, so React never sees it; only the user's name, roles and permissions are kept, in memory. Nothing about the session (no password, no token) is written to `localStorage` or `sessionStorage`. The API client fetches an anti-forgery token from `GET /api/auth/csrf`, sends it on every `POST`/`PATCH`, fetches a new one after login and logout, and refetches once if the server refuses a token. A `401` on any later request returns the user to the login page with "Your session has ended", and a `403` shows the server's message ("Your role does not allow this action.").

**Role-aware screens:** the app hides what the signed-in user's `permissions` (from `/api/auth/me`) do not allow: the product form and edit buttons need `ManageProducts`; receiving and adjusting stock need `ManageStock`; the new-order form and submit/cancel need `ManageOrders`; pick, pack and ship need `FulfillOrders`. Hiding is a courtesy only; the API enforces every permission.

**Shared view of orders:** both pages read their lists from the server (`GET /api/orders`), so every employee sees the same orders and each other's changes after a refresh. Nothing about orders is stored in the browser.

**Run and test** (from `frontend/`; the API and PostgreSQL must be running for `npm run dev`):

| Command | What it does |
|---|---|
| `npm run dev` | Starts the app on http://localhost:5173, forwarding `/api` to http://localhost:5111 (override with `VITE_API_TARGET`) |
| `npm test` | Vitest + React Testing Library unit and component tests (API mocked) |
| `npm run build` | Type-checks and builds |
| `npm run e2e` | Playwright browser tests (48) against their own APIs in the `Testing` environment (ports 5290 and 5291), apps (5273 and 5274) and database `techstore_e2e`, which is dropped and recreated on every run. Test accounts for each role get a random password per run (never written to disk); the shared sessions are saved under `test-results/`. The second ("strict") API has a 1-minute idle timeout and the default login rate limit, and runs last (about 70 s of real waiting). Needs PostgreSQL on localhost:5432, `psql`, the .NET SDK and the `dotnet-ef` tool |

The whole application also runs in Docker; setup, Compose commands and all test commands (backend, frontend, Playwright, container tests) are in the root `README.md`.

**First run of the API** (new in Step 10: nobody can use the API until an Admin exists). From the repository root, with PostgreSQL running:

```bash
dotnet ef database update --project backend/database
dotnet user-secrets set "Auth:InitialAdmin:Email" "you@example.com" --project backend/controllers
dotnet user-secrets set "Auth:InitialAdmin:Password" "<at least 12 characters>" --project backend/controllers
dotnet run --project backend/controllers
```

The user-secrets are stored outside the repository, are read only when no Admin exists, and are never logged. Once the Admin has been created they can be removed (`dotnet user-secrets clear --project backend/controllers`). In any other environment supply the same two values as the environment variables `Auth__InitialAdmin__Email` and `Auth__InitialAdmin__Password`.

---

## 7. Out of Scope for the First Version

Online customer storefront, payment processing, real carrier tracking, returns, stock transfers between warehouses, microservices, AI features. Each order uses one warehouse, and shipping is a simulated confirmation.

---

## 8. Running in Containers (Docker Compose)

`compose.yaml` runs the whole application as four services on one private network. Commands, configuration and troubleshooting are in the root `README.md`; this section explains how the pieces fit. (Step 11.)

### 8.1 The four services

| Service | Image (built from) | Responsibility | Reachable from | Persistent data |
|---|---|---|---|---|
| `web` | `frontend/Dockerfile`: Node builds the React app, then non-root `nginx-unprivileged` (uid 101) | Serves the built React files; answers every non-file URL with `index.html` so client-side routes (`/login`, `/orders`, ...) survive a refresh; forwards `/api/*` and `/health` to `api`; adds the static-site security headers | your computer only (`127.0.0.1:8080`) | none |
| `api` | `backend/Dockerfile` target `runtime`: ASP.NET Core runtime, non-root (uid 1654) | Controllers → services → EF Core; Identity cookie sessions; connects to PostgreSQL as the limited user `techstore_app`; creates the first Admin | `web` only (internal port 8080) | `keys` volume: Data Protection key ring |
| `migrate` | `backend/Dockerfile` target `migrate`: `postgres:17-alpine` with `psql` and two SQL scripts | One-shot job, runs as the database owner: applies missing EF Core migrations (`migrate.sql`, generated with `dotnet ef migrations script --idempotent`), then creates or refreshes `techstore_app` and its grants (`app-role.sql`); exits 0 | nothing | none |
| `db` | `postgres:17` | PostgreSQL; password authentication from the network | `api` and `migrate` only | `pgdata` volume: all business data and accounts |

### 8.2 Networking

- One bridge network, `backend` (default `172.28.0.0/24`). Containers reach each other by service name through Docker's DNS (`api:8080`, `db:5432`).
- Only `web` publishes a port, bound to `127.0.0.1`. `api` and `db` are not reachable from outside Docker.
- `web` has a **fixed address** (`172.28.0.10`). The API is told to believe `X-Forwarded-For` from that one address only (`Auth:TrustedProxies`). nginx **overwrites** the header with the address that connected to it, and the API uses only the last entry, so a client cannot invent its address for the login rate limit.
- nginx looks the API's name up again every 10 seconds, so recreating `api` does not require restarting `web`.

### 8.3 Request flow in the containers

1. The browser requests `http://localhost:8080/...` from `web`. A file (JS, CSS, icon) is served directly; any other path returns `index.html` and React Router renders the page.
2. React calls `/api/...` on the **same origin** (relative URLs; no container address is in browser code). The session and CSRF cookies are therefore ordinary same-site cookies and no CORS is configured.
3. nginx forwards the request, with the cookies and the `X-CSRF-TOKEN` header untouched, to `api:8080`.
4. `api` authenticates the cookie, checks the role's permission, and runs Controller → Service → `DbContext`.
5. EF Core talks to `db:5432` as `techstore_app` (SELECT/INSERT/UPDATE, transactions, row locks as in `services.md`) and the result returns along the same path: PostgreSQL → `DbContext` → Service → Controller → nginx → browser → React updates the screen.

### 8.4 Startup order and restarts

```
db (healthy: pg_isready) → migrate (exit 0) → api (healthy: GET /health) → web (healthy: GET /healthz)
```

- `db` becomes healthy when PostgreSQL accepts connections.
- `migrate` waits for `db`, applies only missing migrations, re-applies the limited user's grants and password, and exits. If it fails (non-zero exit) `api` does not start.
- `api` starts after `migrate` succeeded. At startup `AuthInitializer` (`services.md` §15.2) ensures the roles exist and creates the first Admin from `TECHSTORE_ADMIN_*` only if none exists; with a brand-new database and no Admin settings it refuses to start with a clear message. `GET /health` checks the database connection.
- `web` starts when `api` is healthy.
- `restart: unless-stopped` brings `db`, `api` and `web` back after a crash or a Docker restart. Migrations run again on every `docker compose up` and change nothing when nothing is missing. Data (`pgdata`) and sessions (`keys`) survive `stop`, `restart`, `down` and rebuilds; only `docker compose down -v` deletes them.

### 8.5 Configuration and secrets

`.env` (git-ignored; template `.env.example`) → Compose interpolation in `compose.yaml` → container environment variables → ASP.NET Core configuration (`Auth__InitialAdmin__Email` is the setting `Auth:InitialAdmin:Email`). Required: `POSTGRES_PASSWORD` (database owner, used by `db` and `migrate`) and `TECHSTORE_APP_DB_PASSWORD` (the API's limited user, applied by `migrate`). The first Admin's email and password are needed for the first start only. Nothing secret is built into an image. The full list of settings is in `api-contract.md` §10.8; the production checklist is `deployment-security-checklist.md`.

### 8.6 Development, local Compose and real deployment

| | `dotnet run` + `npm run dev` | Compose stack (this repository) | Real deployment |
|---|---|---|---|
| Frontend served by | Vite dev server (hot reload, proxies `/api`) | nginx, production build | nginx behind HTTPS |
| Database | Your PostgreSQL, usually with `trust` authentication | PostgreSQL with passwords; owner + limited user | Same, on a protected network, with backups |
| Environment | `Development` | `Production` | `Production` |
| Session cookie | Not `Secure` (Development) | Not `Secure`, only because `compose.yaml` sets the two explicit local-only settings; startup warning | `Secure`: the API refuses to start otherwise |
| First Admin | user-secrets or environment variables | `.env` | secret store |
| Test accounts | none (only in the `Testing` environment, used by automated tests) | none | none |
| Employee accounts beyond the first Admin | not available yet (see `README.md`) | not available yet | not available yet |

### 8.7 Tests for the containers

`tests/containers/web-image.sh` (nginx routing, headers, proxying, forwarded-header overwrite, cookies), `compose-stack.sh` (startup, migrations, limited user, login, persistence, secrets), `compose-e2e.sh` (a real browser on the real stack before and after recreating every container) and `backup-restore.sh` (backup and restore on disposable stacks). Commands: `README.md` → "Running the tests".

---

*Entity definitions are in `docs/entities.md`. Detailed service logic, database operations and testing constraints are in `docs/services.md`; the HTTP endpoints are in `docs/api-contract.md`.*
