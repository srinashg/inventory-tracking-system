# TechStore Requirements

This document defines what the application must do (functional), how well it must do it (nonfunctional), how each requirement is checked (acceptance criteria), and what is in or out of the first version. Workflows and API behavior are described in `architecture.md`; this document does not repeat them, it makes them testable.

Requirement IDs (F1, NFR1, …) follow the project blueprint so they can be traced from the blueprint to tests.

---

## 1. Functional Requirements

### F1. Products

The system must let employees create, view, edit and deactivate products.

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F1.1 | Create a product with SKU, name and price. | A valid request returns `201` with the new product ID; the product appears in `GET /api/products`. |
| F1.2 | SKUs are unique. | Creating or editing a product to use an existing SKU returns `409` and changes nothing. |
| F1.3 | Reject invalid product data. | Missing/blank SKU or name, SKU over 50 characters, name over 255 characters, or a negative price returns `400`. |
| F1.4 | Edit product details. | `PATCH` updates only the supplied fields; an unknown ID returns `404`. |
| F1.5 | Deactivate a product. | An inactive product stays in the list and in past orders but is rejected (`409`) when added to a new order. |
| F1.6 | Prices have two decimal places. | A price saved as `1299.99` is returned as `1299.99` with no rounding drift. |

### F2. Warehouses

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F2.1 | Maintain a list of warehouses (name, location). | `GET /api/warehouses` returns all warehouses. |
| F2.2 | An order uses exactly one warehouse. | Creating an order without a warehouse returns `400`; an unknown warehouse returns `404`. |

### F3. Inventory visibility

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F3.1 | Show on-hand, reserved and available quantity per product and warehouse. | `GET /api/inventory?warehouseId=` returns all three values, and `available = onHand − reserved` for every row. |
| F3.2 | One balance per product–warehouse pair. | A second balance for the same pair cannot be created (enforced by the database). |
| F3.3 | Quantities are never negative. | No operation can leave `onHand`, `reserved` or `available` below 0; a database check constraint backs this up. |

### F4. Stock updates

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F4.1 | Receive stock into a warehouse. | Receiving 10 units into an empty balance gives 10 on hand / 0 reserved / 10 available. |
| F4.2 | Receipt quantity must be positive. | Quantity ≤ 0 returns `400` and changes nothing. |
| F4.3 | Adjust stock with a mandatory reason. | An adjustment without a reason returns `400`. |
| F4.4 | An adjustment may not take on-hand below reserved. | Such a request returns `409` and changes nothing. |
| F4.5 | Every stock change is recorded. | Each receipt, adjustment, reservation change and shipment creates an inventory movement row with product, warehouse, quantity change, type, reason and timestamp. |

### F5. Orders (drafts)

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F5.1 | Create a draft order with a customer, warehouse and one or more items. | Returns `201` with order ID, status `Draft` and total units. |
| F5.2 | Creating a draft does not touch stock. | On hand and reserved are identical before and after. |
| F5.3 | Item quantities must be positive; an order needs at least one item; a product may appear on only one line. | Violations return `400`. |
| F5.4 | Each item stores the product price at order time. | After a product's price is changed, an existing order still shows the original unit price. |
| F5.5 | Unknown customer, warehouse or product is rejected. | Returns `404`. |
| F5.6 | View an order. | `GET /api/orders/{id}` returns items and current status; unknown ID returns `404`. |
| F5.7 | List orders for everyone, filtered by status, customer or warehouse. | `GET /api/orders` returns every order newest first (paged), `status` can be repeated, and an invalid filter returns `400`. An order created by one user is listed for any other user. |

### F6. Reservations

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F6.1 | Submitting a `Draft` order reserves stock for every line. | Submitting a 2-unit order with 10 available leaves 10 on hand / 2 reserved / 8 available and status `Reserved`. |
| F6.2 | Reserve all lines or none. | If any line lacks stock, the response is `409`, no line is reserved, and the order stays `Draft`. |
| F6.3 | Only a `Draft` can be submitted. | Submitting any other status returns `409`. |
| F6.4 | No overselling under concurrency. | See NFR1. |
| F6.5 | Reservations are recorded atomically with the status change. | A forced failure mid-submit leaves stock, movements and status all unchanged. |

### F7. Fulfillment

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F7.1 | Orders move `Reserved → Picking → Packed → Shipped` in that order only. | Every other transition returns `409`. |
| F7.2 | Pick and pack do not change stock. | Quantities are identical before and after each. |
| F7.3 | Shipping reduces on hand and reserved by each line's quantity. | Shipping the 2-unit order moves stock from 10/2/8 to 8/0/8. |
| F7.4 | Shipping happens once. | Sending ship twice returns `409` on the second call and deducts only once. |
| F7.5 | Shipping is recorded atomically. | Stock update, movement rows and status change commit together or not at all. |

### F8. Cancellations

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F8.1 | Cancel a `Draft` order. | Status becomes `Canceled`; stock unchanged. |
| F8.2 | Cancel a `Reserved` order and release its reservation. | Reserved decreases and available increases by the order's quantities; status becomes `Canceled`. |
| F8.3 | Other statuses cannot be canceled. | `Picking`, `Packed`, `Shipped` and `Canceled` orders return `409` and nothing changes. |

### F9. Dashboard

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F9.1 | Show a summary: available stock, low-stock items, open orders, shipped orders. | `GET /api/dashboard/summary` returns these totals, and they match counts computed directly from the data in tests. |
| F9.2 | Define "low stock" with a stated threshold. | Low stock means `available ≤ 5` for an active product in a warehouse (`services.md` §9); the dashboard response reports the threshold used. |

### F10. Authentication and authorization (Step 10)

Roles and the endpoint-by-endpoint permission matrix: `api-contract.md` §2.1. Authentication mechanics: `api-contract.md` §10.

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F10.1 | Employees sign in with email and password. There is no public registration. | `POST /api/auth/login` with valid credentials returns `200`, the user's roles and permissions, and an HttpOnly session cookie. No endpoint creates users. |
| F10.2 | Invalid credentials are rejected without revealing why. | A wrong password, an unknown email and a locked account all return the same `401` body, and an unknown email costs the same hashing work so response time does not reveal which accounts exist. 5 consecutive failures lock the account for 15 minutes, and a correct password during lockout is refused. |
| F10.3 | Every endpoint except `/health`, `GET /api/auth/csrf` and `POST /api/auth/login` requires a signed-in user. | An anonymous request to each other endpoint returns `401` (checked for 100% of endpoints, so a newly added endpoint is covered automatically). |
| F10.4 | Permissions follow the three roles. | Each role calls every endpoint in `api-contract.md` §2 and gets success where the matrix has ✓ and `403` where it has ✗. Admin: everything. Sales: view, plus create, submit and cancel orders. Warehouse: view, plus receive and adjust stock, and pick, pack and ship. |
| F10.5 | The server enforces permissions; the UI only reflects them. | A forbidden request sent directly to the API (bypassing React) returns `403`. The React app hides or disables actions the signed-in user's `permissions` do not include. |
| F10.6 | Users can sign out, and signing out ends all of their sessions. | After `POST /api/auth/logout` the cookie is cleared and `GET /api/auth/me` returns `401`; a copied cookie, and the same user's session in another browser, also return `401`; other users' sessions are unaffected; anonymous or CSRF-invalid logout requests revoke nothing. |
| F10.7 | Sessions end when they should. | A session expires after 8 hours without activity, and after 12 hours in total even if the user is active (both configurable; tested with a controllable clock). Changing a user's password, role or locked state ends that user's existing sessions: the next request returns `401`. (Identity rotates its security stamp only for password changes; `AccountService` rotates it explicitly for administrator role and lock changes. Verified, see `api-contract.md` §10.6.) The stamp is checked on every request by default, and the interval is configurable (`Auth:SessionRevalidationSeconds`). |
| F10.8 | Cookie sessions are protected against CSRF. | A `POST`/`PATCH` without a valid `X-CSRF-TOKEN` returns `400`, including login and logout. A token fetched before login is rejected after login. |
| F10.9 | Credentials are handled securely and misconfiguration is flagged. A session re-check interval above 60 seconds, and bootstrap settings left behind after an Admin exists, each log a warning that contains no secret.  | Passwords are stored only as salted hashes. If no Admin exists and no bootstrap credentials are configured, startup fails with a clear error that contains no secret; if an Admin exists, startup succeeds without them. The first Admin password comes from environment variables or user-secrets and is never committed, returned or logged. Test accounts exist only in the `Testing` environment (startup fails if they are requested elsewhere). |
| F10.10 | Sign-in is rate limited and passwords are not trivially weak. | The 11th sign-in attempt in a minute from one client returns `429` with `Retry-After`; a configurable global cap (default 100 per minute) does the same across clients; a forged `X-Forwarded-For` does not change who is counted. Passwords with fewer than 5 different characters, common fragments, or the email name are refused without being repeated in the message. |
| F10.11 | API responses are not cached and carry protective headers. | Every `/api` response has `Cache-Control: no-store`; every response has `nosniff`, `Referrer-Policy`, `X-Frame-Options` and a framing-blocking content-security policy, including errors and `429`. |
| F10.12 | Authentication and authorization work end to end in a real browser. | Playwright tests (Testing environment, isolated `techstore_e2e` database, random-password test accounts) cover: login/logout and protected-route redirects; per-role visible actions; `401` for anonymous and `403` for unauthorized calls on every endpoint; CSRF refusals; session revocation after logout; session idle timeout; login rate limit; the full receive-order-fulfil workflow across roles; insufficient stock leaving inventory unchanged. |

### F11. Health

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F11.1 | Expose a health endpoint. | `GET /health` returns a healthy response when the API and database are reachable. |

### F12. Containerized local deployment and operations (Step 11)

How to run it: `README.md`. How it is built: `architecture.md` §8. What is still required for production: `deployment-security-checklist.md`.

| ID | Requirement | Acceptance criteria |
|---|---|---|
| F12.1 | One command starts the whole application from a clean checkout. | After `cp .env.example .env`, filling the blanks and `docker compose up --build -d`, the `db`, `api` and `web` services are healthy, `migrate` has exited 0, `GET /health` through the web server returns `Healthy`, and the Admin can sign in (`compose-stack.sh`, `compose-e2e.sh`, and a clean-copy walkthrough of the README). |
| F12.2 | Services start in a reliable order. | `api` starts only after the database is healthy and `migrate` has completed successfully; `web` only after `api` is healthy. |
| F12.3 | Database migrations are automatic, idempotent and never destructive. | Running `migrate` again, by restart or by hand, changes no record and no migration count; the migration script contains no `DROP`, `TRUNCATE` or `DELETE FROM` (the stack test fails if one appears). |
| F12.4 | The API uses a least-privilege database user. | `techstore_app` is not a superuser and cannot create/drop/alter tables, create roles or databases, run `COPY ... PROGRAM`, write the migration history, or delete business records; it can complete the full receive → order → ship workflow; tables added by later migrations are usable without another grant (`LeastPrivilegeDatabaseTests`, `compose-stack.sh`). Provisioning is safe to repeat and changes the password when the setting changes. |
| F12.5 | The first Admin is created securely from configuration and the settings can be removed. | With no Admin and no settings the API refuses to start with a message that names settings but no values; with them it creates exactly one Admin; after the settings are blanked and the API container recreated the Admin still signs in, the container no longer holds the password, and no warning is logged. |
| F12.6 | Data and sessions survive restarts. | After `docker compose restart` and after `down` then `up` (volumes kept), all records are identical, the key ring is the same, and sessions saved before the restart still work without signing in again. Only `down -v` removes data. |
| F12.7 | The browser reaches the API through one origin, and the client address cannot be forged. | Refreshing `/login`, `/orders` and unknown routes returns the app; `/api` and `/health` are forwarded with cookies and the CSRF header intact; no internal address appears in the JavaScript; a forged `X-Forwarded-For` or `X-Real-IP` does not reach the API and does not avoid the login rate limit (`web-image.sh`, `compose-stack.sh`). |
| F12.8 | Secrets are never in the repository, images or logs. | The generated database, application-user and Admin passwords are found in no container log, no image environment or history, and no file in the repository (`compose-stack.sh`, `compose-e2e.sh`). |
| F12.9 | The local-only plain-http cookie exception cannot silently reach a real deployment. | Outside Development/Testing the API refuses `Auth:RequireSecureCookie=false` unless `Auth:AllowInsecureCookiesForLocalHttp=true` is also set, logs a warning when it is, and the Compose stack publishes its port on `127.0.0.1` only (`DeploymentConfigApiTests`, `AuthInitializerTests`, `compose-stack.sh`). |
| F12.10 | The database can be backed up and restored safely. | The documented backup and restore commands recover every table and account (verified by a per-table fingerprint) in place and into a brand-new stack with different secrets; a damaged backup file leaves the database unchanged; the app works afterwards as the limited user (`backup-restore.sh`). |
| F12.11 | Another developer can set up, run and test the application from the documentation alone. | The README commands were followed verbatim on a clean copy of the repository (stack, backup, `down` and `down -v`, and the non-Docker development flow) with no undocumented step. |

---

## 2. Nonfunctional Requirements

| ID | Quality | Requirement | Acceptance criteria |
|---|---|---|---|
| NFR1 | **Correctness** | Stock is never oversold, and order lines are all-or-nothing. | In an automated test, 100 concurrent reservation attempts against the same 10 units result in 0 oversells (reserved ≤ on hand, no negative available). |
| NFR2 | **Reliability** | Multi-step changes never partially persist; actions are idempotent where required. | Forced-failure transaction tests show 0 partial writes; a repeated ship request never deducts twice. |
| NFR3 | **Security** | Endpoints are protected and no secrets are stored in the repository. | Anonymous requests to protected endpoints return `401` and wrong-role requests return `403` (100% of protected endpoints); an automated test finds no password, key or token in committed configuration (including `appsettings*.json` and test configuration); passwords, hashes and tokens never appear in logs; the session cookie is HttpOnly, `SameSite=Strict` and `Secure` outside local development; nothing authentication-related is stored in `localStorage`. |
| NFR4 | **Performance** | The system stays responsive on a realistic data size. | With 10,000 products and 1,000 orders on a local machine, 95% of read requests finish in under 300 ms and 95% of reservation requests in under 750 ms. |
| NFR5 | **Maintainability** | Business rules live in services, not controllers. | Controllers contain no database queries or business rules (checked in code review); the flow is Controller → Service → DbContext → Database. |
| NFR6 | **Testability** | Business logic is covered by automated tests. | At least 80% line coverage on service classes; every permitted and every blocked order-status transition has a test. |
| NFR7 | **Observability** | Stock changes are traceable. | 100% of stock-changing actions log operation, outcome and order/stock reference, with no passwords in logs. |
| NFR8 | **Usability** | The core flow is quick to learn and works on small screens. | In a guided first-time test, a user completes receive → reserve → ship in 5 minutes; pages are readable at a 375 px viewport. |
| NFR9 | **Data integrity** | The database enforces the key rules even if application code is bypassed. | Unique constraints on SKU and (product, warehouse); check constraints for non-negative stock and positive order quantity; money stored as `numeric(12,2)`; foreign keys on all relations. |
| NFR10 | **Error handling** | Failures are reported clearly and consistently. | Errors use the documented status codes (400/401/403/404/409) with a readable message; unexpected errors return `500` without exposing internal details. |
| NFR11 | **Operability** | The deployment is reproducible, observable and recoverable. | Services report health (`/health`, `/healthz`); the stack is rebuilt from the repository with no manual step; backups and restores follow the README and pass `backup-restore.sh`; the remaining production requirements are listed in `deployment-security-checklist.md`. |

---

## 3. Scope Boundaries

### Included in the first version

- Internal employee tool: one React app, one ASP.NET Core API, one PostgreSQL database (monolith).
- Products, warehouses, customers (sample data), inventory, stock receipts and adjustments.
- Draft orders, reservation, pick/pack/ship, cancellation, and dashboard summary.
- One warehouse per order; shipping is a simulated confirmation.
- Login, logout and role-based authorization (Admin, Sales, Warehouse) using ASP.NET Core Identity and cookie sessions (Step 10).
- Automated tests (xUnit unit and integration, Vitest, Playwright, container tests), structured logging, health endpoint.
- Local containerized deployment with Docker Compose (web, api, migrate, db), a least-privilege application database user, persistent volumes, and documented backup and restore (Step 11).
- Delivery follows the order in `CLAUDE.md`: docs → schema → database → services (TDD) → controllers → frontend → hardening.

### Deferred (not in this version)

- Online customer storefront and customer accounts.
- Payment processing.
- Real carrier or shipment tracking.
- Returns and refunds.
- Stock transfers between warehouses and orders split across warehouses.
- Editing a draft order's items after creation (cancel and recreate instead).
- Cancelling orders that are already `Picking` or `Packed`.
- Admin screens, endpoints or a command-line tool to create or manage employee accounts, and password change or reset screens (only the first Admin is created, from configuration, in Step 10). Direct SQL account creation is used only by isolated tests and is not a supported method.
- Multi-factor authentication and single sign-on (the design leaves room to add an external identity provider later).
- Microservices, message queues, and AI features.
- Production hardening of the container deployment (HTTPS termination, encrypted key ring, file-based secrets, automated backups, high availability): see `deployment-security-checklist.md`.
- CI/CD and cloud hosting (optional later: GitHub Actions CI and an Azure demo).

---

## 4. Traceability

| Blueprint | Requirements | Covered in |
|---|---|---|
| F1–F10 (feature table) | F1–F10 above | `architecture.md` §3; F10 in `api-contract.md` §2.1 and §10 |
| Nonfunctional table | NFR1–NFR8 above | Tests in Step 6 and Step 8 |
| Authentication (Step 10) | F10, NFR3 | `api-contract.md` §2.1 and §10; `services.md` §15; Playwright `e2e/` |
| Containerized deployment (Step 11) | F12, NFR11 | `architecture.md` §8; `README.md`; `tests/containers/` |
| Rules to protect the data | F3.2, F3.3, F5.4, F6.2, F6.5, F7.5, NFR9 | Schema in Step 3 |
| Definition of done / final acceptance scenario | F4.1, F5.1, F6.1, F6.2, F7.1–F7.4, F8.2 | `architecture.md` §5 |

---

## 5. Open Items to Confirm

These are carried over from `architecture.md` and are the author's assumptions, not blueprint statements:

1. Cancelling `Picking` or `Packed` orders is not allowed.
2. Inactive products cannot be added to new orders.
3. A stock adjustment may not reduce on hand below the reserved quantity.
4. The low-stock threshold (5) is defined in `services.md` §9, and the role-to-permission matrix in `api-contract.md` §2.1; both are open to change at review.
