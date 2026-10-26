# TechStore API Contract

The HTTP interface between the React frontend and the ASP.NET Core backend. Each endpoint maps to one service operation in `services.md`; validation rules and business logic are defined there and are not repeated here. Workflows are in `architecture.md`.

---

## 1. Conventions

| Topic | Rule |
|---|---|
| Base path | All endpoints start with `/api`, except `GET /health` |
| Format | JSON requests and responses (`Content-Type: application/json`), property names in `camelCase` |
| IDs | Numbers (64-bit integers) |
| Money | JSON numbers with 2 decimals, for example `1299.99` |
| Timestamps | ISO 8601 in UTC, for example `2026-10-25T18:30:00Z` |
| Enums | Sent as strings: order status is one of `Draft`, `Reserved`, `Picking`, `Packed`, `Shipped`, `Canceled` |
| Paging | `page` (default 1) and `pageSize` (default 50, max 200) on list endpoints that say so |
| Authentication | Cookie session (HttpOnly, `SameSite=Strict`) created by `POST /api/auth/login`; see section 10. Every endpoint except those marked **Anonymous** in section 2 requires a signed-in user. |
| CSRF protection | Every `POST`, `PATCH` and `DELETE` request must send an `X-CSRF-TOKEN` header obtained from `GET /api/auth/csrf` (section 10.5). `GET` requests do not. |

### 1.1 Paged responses

```json
{ "items": [ ... ], "page": 1, "pageSize": 50, "totalCount": 123 }
```

### 1.2 Error responses

All errors use the standard ASP.NET Core `ProblemDetails` shape (`application/problem+json`):

```json
{
  "status": 409,
  "title": "Conflict",
  "detail": "Insufficient stock for SKU LAP-1: requested 15, available 10."
}
```

Input errors (`400`) add an `errors` object listing the problems per field:

```json
{
  "status": 400,
  "title": "Validation failed",
  "detail": "One or more fields are invalid.",
  "errors": { "quantity": ["Quantity must be between 1 and 1,000,000."] }
}
```

| Status | When |
|---|---|
| `400 Bad Request` | Invalid input (missing or out-of-range fields, malformed JSON, empty body on PATCH), or a missing or invalid `X-CSRF-TOKEN` on a `POST`/`PATCH` |
| `401 Unauthorized` | Not signed in, the session expired or was ended, or the credentials sent to login are wrong |
| `403 Forbidden` | Signed in, but the user's role does not allow this action (section 2.1) |
| `404 Not Found` | A referenced order, product, customer, warehouse or balance does not exist |
| `409 Conflict` | Valid request that current state forbids: duplicate SKU, inactive product, insufficient stock, wrong order status, adjustment below reserved stock |
| `429 Too Many Requests` | Too many sign-in attempts (section 10.2); the `Retry-After` header says how many seconds to wait |
| `500 Internal Server Error` | Unexpected failure; generic message, details only in server logs |

Malformed JSON or a missing body also returns this `400` shape (the `errors` keys then name the offending JSON path or property). A route that does not exist, or an `{id}` that is not a number, returns `404`; a wrong HTTP method on a real route returns `405`. A `500` body is always the generic `{"status":500,"title":"Internal Server Error","detail":"An unexpected error occurred."}`.

Error cases below list only the statuses specific to that endpoint. `401` applies to every endpoint not marked Anonymous, and `403` to every endpoint that needs a specific permission (section 2). A missing or invalid CSRF token returns `400` on any `POST`/`PATCH`.

### 1.3 Shared response shapes

**ProductResponse**
```json
{ "productId": 101, "sku": "LAP-1", "name": "Laptop 14\"", "price": 1299.99, "isActive": true }
```

**WarehouseResponse**
```json
{ "warehouseId": 1, "name": "Dallas", "location": "Dallas, TX" }
```

**CustomerResponse**
```json
{ "customerId": 201, "name": "Ann Lee", "email": "ann@example.com" }
```

**InventoryItemResponse**
```json
{ "productId": 101, "sku": "LAP-1", "name": "Laptop 14\"", "onHand": 10, "reserved": 2, "available": 8 }
```

**StockChangeResponse**
```json
{ "movementId": 55, "productId": 101, "warehouseId": 1, "onHand": 10, "reserved": 0, "available": 10 }
```

**OrderListItem** (one row of `GET /api/orders`: an order without its lines)
```json
{ "orderId": 1001, "customerId": 201, "warehouseId": 1, "status": "Reserved", "createdAt": "2026-10-25T18:30:00Z", "totalUnits": 2, "totalAmount": 2599.98 }
```

**OrderResponse** (returned by every other order endpoint)
```json
{
  "orderId": 1001,
  "customerId": 201,
  "warehouseId": 1,
  "status": "Draft",
  "createdAt": "2026-10-25T18:30:00Z",
  "totalUnits": 2,
  "totalAmount": 2599.98,
  "items": [
    { "productId": 101, "sku": "LAP-1", "name": "Laptop 14\"", "quantity": 2, "unitPrice": 1299.99 }
  ]
}
```

---

## 2. Endpoint Summary

| # | Method and path | Purpose | Service operation | Access | Admin | Sales | Warehouse |
|---|---|---|---|---|---|---|---|
| 1 | `GET /api/products` | List products | `ProductService.ListProducts` | Signed in | ✓ | ✓ | ✓ |
| 2 | `POST /api/products` | Add product | `ProductService.CreateProduct` | ManageProducts | ✓ | ✗ | ✗ |
| 3 | `PATCH /api/products/{id}` | Edit or deactivate product | `ProductService.UpdateProduct` | ManageProducts | ✓ | ✗ | ✗ |
| 4 | `GET /api/warehouses` | List warehouses | `ReferenceDataService.ListWarehouses` | Signed in | ✓ | ✓ | ✓ |
| 5 | `GET /api/customers` | List customers | `ReferenceDataService.ListCustomers` | Signed in | ✓ | ✓ | ✓ |
| 6 | `GET /api/inventory` | View warehouse stock | `InventoryService.GetInventory` | Signed in | ✓ | ✓ | ✓ |
| 7 | `POST /api/inventory/receipts` | Receive stock | `InventoryService.ReceiveStock` | ManageStock | ✓ | ✗ | ✓ |
| 8 | `POST /api/inventory/adjustments` | Correct stock | `InventoryService.AdjustStock` | ManageStock | ✓ | ✗ | ✓ |
| 9 | `POST /api/orders` | Create draft order | `OrderService.CreateDraftOrder` | ManageOrders | ✓ | ✓ | ✗ |
| 10 | `GET /api/orders` | List orders | `OrderService.ListOrders` | Signed in | ✓ | ✓ | ✓ |
| 11 | `GET /api/orders/{id}` | Open order | `OrderService.GetOrder` | Signed in | ✓ | ✓ | ✓ |
| 12 | `POST /api/orders/{id}/submit` | Reserve stock | `ReservationService.SubmitOrder` | ManageOrders | ✓ | ✓ | ✗ |
| 13 | `POST /api/orders/{id}/pick` | Start picking | `FulfillmentService.PickOrder` | FulfillOrders | ✓ | ✗ | ✓ |
| 14 | `POST /api/orders/{id}/pack` | Finish packing | `FulfillmentService.PackOrder` | FulfillOrders | ✓ | ✗ | ✓ |
| 15 | `POST /api/orders/{id}/ship` | Confirm shipment | `FulfillmentService.ShipOrder` | FulfillOrders | ✓ | ✗ | ✓ |
| 16 | `POST /api/orders/{id}/cancel` | Cancel order | `ReservationService.CancelOrder` | ManageOrders | ✓ | ✓ | ✗ |
| 17 | `GET /api/dashboard/summary` | Dashboard totals | `DashboardService.GetSummary` | Signed in | ✓ | ✓ | ✓ |
| 18 | `GET /health` | Readiness check | `ASP.NET Core health checks` | Anonymous | – | – | – |
| 19 | `GET /api/auth/csrf` | Get an anti-forgery token | `AccountService (section 10.1)` | Anonymous | – | – | – |
| 20 | `POST /api/auth/login` | Sign in | `AccountService.LoginAsync` | Anonymous | – | – | – |
| 21 | `POST /api/auth/logout` | Sign out | `AccountService.LogoutAsync` | Signed in | ✓ | ✓ | ✓ |
| 22 | `GET /api/auth/me` | Who am I and what may I do | `AccountService.GetCurrentUserAsync` | Signed in | ✓ | ✓ | ✓ |

**Access** names the authorization rule ASP.NET Core applies (section 2.1). ✓ means the role may call the endpoint; ✗ means it receives `403`. "Signed in" means any of the three roles; **Anonymous** endpoints need no session. Anything not listed here is denied to anonymous callers by default.

### 2.1 Roles and permissions

Three roles exist. Every employee has exactly one role in the first version.

| Role | May do |
|---|---|
| **Admin** | Everything: every endpoint in the table above |
| **Sales** | View products, inventory, customers, warehouses, orders and the dashboard; create, submit and cancel orders |
| **Warehouse** | View products, inventory, customers, warehouses, orders and the dashboard; receive and adjust stock; pick, pack and ship orders |

Permissions are expressed as named **policies**, so a controller says what it protects (not which roles may use it) and the role-to-policy mapping lives in one place:

| Policy | Roles allowed | Protects |
|---|---|---|
| *Signed in* (the default for every endpoint) | Admin, Sales, Warehouse | All `GET` endpoints, plus logout and `me` |
| `ManageProducts` | Admin | `POST /api/products`, `PATCH /api/products/{id}` |
| `ManageStock` | Admin, Warehouse | `POST /api/inventory/receipts`, `POST /api/inventory/adjustments` |
| `ManageOrders` | Admin, Sales | `POST /api/orders`, `POST /api/orders/{id}/submit`, `POST /api/orders/{id}/cancel` |
| `FulfillOrders` | Admin, Warehouse | `POST /api/orders/{id}/pick`, `/pack`, `/ship` |

Rules:
- **Enforced by ASP.NET Core, not by the UI.** The React app hides buttons a user cannot use, but the API refuses the request regardless of what the browser sends.
- **Secure by default.** An endpoint with no explicit rule requires a signed-in user. Only `/health`, `GET /api/auth/csrf` and `POST /api/auth/login` are anonymous.
- **`401` vs `403`.** `401` means "we don't know who you are" (no session, expired, ended, wrong password). `403` means "we know who you are and the answer is no". A Sales user calling `POST /api/inventory/adjustments` gets `403`.
- **Read access is shared.** Sales users can see the fulfillment queue and stock levels (read-only); they just cannot act on them.
- **Services stay role-agnostic.** Business rules live in the services; authorization sits in front of them, so no service logic changes.

---

## 3. Products

### 3.1 `GET /api/products`

List products, ordered by SKU.

- **Query:** `activeOnly` (bool, default `false`), `page`, `pageSize`
- **Success:** `200 OK` → paged `ProductResponse`
- **Errors:** `400` invalid paging
- **Service:** `ProductService.ListProducts`

### 3.2 `POST /api/products`

Add a product. New products are active.

- **Request:**
  ```json
  { "sku": "LAP-1", "name": "Laptop 14\"", "price": 1299.99 }
  ```
- **Success:** `201 Created`, body `ProductResponse` (includes the new `productId`)
- **Errors:** `400` missing or invalid sku/name/price; `409` SKU already exists
- **Service:** `ProductService.CreateProduct`

### 3.3 `PATCH /api/products/{id}`

Update any subset of fields; omitted fields stay unchanged. At least one field is required.

- **Request** (all fields optional):
  ```json
  { "sku": "LAP-2", "name": "Laptop 15\"", "price": 1399.00, "isActive": false }
  ```
- **Success:** `200 OK` → `ProductResponse`
- **Errors:** `400` empty body or invalid field; `404` unknown product; `409` SKU belongs to another product
- **Service:** `ProductService.UpdateProduct`

---

## 4. Reference Data

### 4.1 `GET /api/warehouses`

- **Success:** `200 OK` → array of `WarehouseResponse`, ordered by name (empty array if none)
- **Service:** `ReferenceDataService.ListWarehouses`

### 4.2 `GET /api/customers`

- **Success:** `200 OK` → array of `CustomerResponse`, ordered by name (empty array if none)
- **Service:** `ReferenceDataService.ListCustomers`

---

## 5. Inventory

### 5.1 `GET /api/inventory`

On-hand, reserved and available stock for one warehouse, ordered by SKU. `available` is always `onHand − reserved`.

- **Query:** `warehouseId` (required), `page`, `pageSize`
- **Success:** `200 OK` → paged `InventoryItemResponse`
- **Errors:** `400` missing or invalid `warehouseId` or paging; `404` unknown warehouse
- **Service:** `InventoryService.GetInventory`

### 5.2 `POST /api/inventory/receipts`

Receive stock. Creates the balance on first receipt and logs a `Receipt` movement.

- **Request:**
  ```json
  { "productId": 101, "warehouseId": 1, "quantity": 10, "note": "Supplier delivery" }
  ```
  `note` is optional.
- **Success:** `201 Created` → `StockChangeResponse`
- **Errors:** `400` quantity outside 1–1,000,000 or missing IDs; `404` unknown product or warehouse; `409` quantity would exceed the maximum storable stock
- **Service:** `InventoryService.ReceiveStock`

### 5.3 `POST /api/inventory/adjustments`

Correct on-hand stock by a signed amount with a reason; logs an `Adjustment` movement. The balance must already exist.

- **Request:**
  ```json
  { "productId": 101, "warehouseId": 1, "quantityChange": -2, "reason": "Damaged in storage" }
  ```
- **Success:** `201 Created` → `StockChangeResponse`
- **Errors:** `400` change is 0 or out of range, reason blank or over 255 characters; `404` unknown product or warehouse, or no balance for the pair; `409` change would take on-hand below the reserved quantity
- **Service:** `InventoryService.AdjustStock`

---

## 6. Orders

### 6.1 `POST /api/orders`

Create a draft order. **Does not reserve stock.** Each line copies the product's current price.

- **Request:**
  ```json
  {
    "customerId": 201,
    "warehouseId": 1,
    "items": [ { "productId": 101, "quantity": 2 } ]
  }
  ```
- **Success:** `201 Created`, `Location: /api/orders/1001`, body `OrderResponse` with `status: "Draft"` (includes `orderId`, `status` and `totalUnits` as in the blueprint example)
- **Errors:** `400` no items, more than 100 items, repeated product, quantity outside 1–1,000,000; `404` unknown customer, warehouse or product; `409` a product is inactive
- **Service:** `OrderService.CreateDraftOrder`

### 6.2 `GET /api/orders/{id}`

- **Success:** `200 OK` → `OrderResponse`
- **Errors:** `404` unknown order
- **Service:** `OrderService.GetOrder`

### 6.3 `POST /api/orders/{id}/submit`

Reserve stock for all lines, or none. `Draft → Reserved`. No request body.

- **Success:** `200 OK` → `OrderResponse` with `status: "Reserved"`
- **Errors:** `404` unknown order; `409` order is not `Draft`, or insufficient stock (detail names the SKU, requested and available quantity; no stock changes and the order stays `Draft`)
- **Service:** `ReservationService.SubmitOrder`

### 6.4 `POST /api/orders/{id}/pick`

`Reserved → Picking`. No request body. Stock does not change.

- **Success:** `200 OK` → `OrderResponse` with `status: "Picking"`
- **Errors:** `404` unknown order; `409` order is not `Reserved`
- **Service:** `FulfillmentService.PickOrder`

### 6.5 `POST /api/orders/{id}/pack`

`Picking → Packed`. No request body. Stock does not change.

- **Success:** `200 OK` → `OrderResponse` with `status: "Packed"`
- **Errors:** `404` unknown order; `409` order is not `Picking`
- **Service:** `FulfillmentService.PackOrder`

### 6.6 `POST /api/orders/{id}/ship`

`Packed → Shipped`. Deducts on-hand and reserved stock once. No request body.

- **Success:** `200 OK` → `OrderResponse` with `status: "Shipped"`
- **Errors:** `404` unknown order; `409` order is not `Packed` (including an order that is already `Shipped`)
- **Service:** `FulfillmentService.ShipOrder`

### 6.7 `POST /api/orders/{id}/cancel`

Cancel a `Draft` or `Reserved` order; a reservation is released. No request body.

- **Success:** `200 OK` → `OrderResponse` with `status: "Canceled"`
- **Errors:** `404` unknown order; `409` order is `Picking`, `Packed`, `Shipped` or already `Canceled`
- **Service:** `ReservationService.CancelOrder`

### 6.8 `GET /api/orders`

List orders, newest first (highest order number first). Returns summaries without the order lines; open an order with `GET /api/orders/{id}` for those.

- **Query:** `status` (optional; repeat it to allow several, e.g. `?status=Reserved&status=Picking`; one of `Draft`, `Reserved`, `Picking`, `Packed`, `Shipped`, `Canceled`, case-insensitive), `customerId` (optional), `warehouseId` (optional), `page` (default 1), `pageSize` (default 50, max 200). Filters combine with AND; several `status` values combine with OR.
- **Success:** `200 OK` → paged `OrderListItem`. No matching orders is a normal `200` with `items: []` and `totalCount: 0`.
- **Errors:** `400` unknown `status` value, non-positive `customerId` or `warehouseId`, or invalid paging
- **Service:** `OrderService.ListOrders`

The valid status transitions behind these endpoints are in `services.md` section 10.

---

## 7. Dashboard

### 7.1 `GET /api/dashboard/summary`

- **Success:** `200 OK`
  ```json
  {
    "totalAvailableUnits": 245,
    "lowStockThreshold": 5,
    "lowStockCount": 2,
    "lowStockItems": [
      { "productId": 101, "sku": "LAP-1", "name": "Laptop 14\"", "warehouseId": 1, "available": 3 }
    ],
    "openOrders": 4,
    "shippedOrders": 12
  }
  ```
  `lowStockItems` holds at most 50 entries, lowest available first. `openOrders` counts `Draft`, `Reserved`, `Picking` and `Packed` orders.
- **Errors:** none specific
- **Service:** `DashboardService.GetSummary`

---

## 8. Health

### 8.1 `GET /health`

- **Success:** `200 OK`, body `Healthy`, when the API is running and can reach PostgreSQL
- **Errors:** `503 Service Unavailable`, body `Unhealthy`, when the database cannot be reached
- **Service:** ASP.NET Core health checks (no TechStore service)

---

## 9. Example: The Main Order Journey

| Step | Request | Result |
|---|---|---|
| Receive 10 laptops | `POST /api/inventory/receipts` `{productId:101, warehouseId:1, quantity:10}` | `201`, onHand 10, reserved 0, available 10 |
| Draft an order for 2 | `POST /api/orders` `{customerId:201, warehouseId:1, items:[{productId:101, quantity:2}]}` | `201`, `Draft`, totalUnits 2 |
| Submit | `POST /api/orders/1001/submit` | `200`, `Reserved`; inventory shows 10 / 2 / 8 |
| Pick, then pack | `POST /api/orders/1001/pick`, `POST /api/orders/1001/pack` | `200`, `Picking` then `Packed` |
| Ship | `POST /api/orders/1001/ship` | `200`, `Shipped`; inventory shows 8 / 0 / 8 |
| Ship again | `POST /api/orders/1001/ship` | `409`, stock unchanged |
| Order 15 with 10 available, then submit | `POST /api/orders/…/submit` | `409` "Insufficient stock…", order stays `Draft`, stock unchanged |

---

## 10. Authentication (Step 10)

Employees sign in with an email and password. The server keeps no session table: a successful login returns an **encrypted, signed cookie** that the browser sends automatically. The cookie holds the user's identity and roles; passwords are never stored or sent again.

### 10.1 `GET /api/auth/csrf`

Anonymous. Returns the anti-forgery token the browser must send with every `POST`/`PATCH`.

- **Success:** `200 OK`
  ```json
  { "token": "CfDJ8..." }
  ```
  and an anti-forgery cookie. The token is tied to the current identity, so fetch a new one **after login and after logout**; a token fetched before login is rejected afterwards.

### 10.2 `POST /api/auth/login`

Anonymous. Requires the CSRF header.

- **Request:**
  ```json
  { "email": "amelia@example.com", "password": "••••••••••••" }
  ```
- **Success:** `200 OK` with `CurrentUser` (below) and the session cookie in `Set-Cookie`.
- **Errors:** `400` missing field or invalid CSRF token; `401` wrong email or password **or** the account is temporarily locked. Both give the same body (`"Invalid email or password."`) so the response does not reveal whether an account exists or is locked.
- **Rate limit:** at most 10 sign-in attempts per minute from one client address (successful ones count too), and 100 per minute across all clients, a cap against password spraying. Beyond that the answer is `429` with `Retry-After`, even for a correct password. The client address is the connection's own address; `X-Forwarded-For` and similar headers are **not** trusted, because they can be forged. Both limits are configurable (section 10.8).
- **Lockout:** 5 consecutive wrong passwords lock the account for 15 minutes; a correct password during lockout is also refused with `401`. A successful login resets the count.
- **No account probing:** an unknown email costs the same password-hashing work as a wrong password, so response time does not reveal which emails have accounts. The text typed into the email box is never logged.
- **Service:** `AccountService.LoginAsync` (wraps ASP.NET Core Identity's `SignInManager`).

### 10.3 `POST /api/auth/logout`

Requires a session and the CSRF header.

- **Success:** `204 No Content`; the session cookie is cleared, and all of the user's other sessions end too (section 10.6).
- **Errors:** `401` no session; `400` invalid CSRF token. Neither revokes anything.
- **Service:** `AccountService.LogoutAsync`

### 10.4 `GET /api/auth/me`

Requires a session. The React app calls it when it loads to find out whether the user is signed in and what to show.

- **Success:** `200 OK`
  ```json
  {
    "userId": 3,
    "email": "amelia@example.com",
    "displayName": "Amelia Reed",
    "roles": ["Sales"],
    "permissions": ["ManageOrders"]
  }
  ```
  `permissions` lists the policies from section 2.1 that this user satisfies (`ManageProducts`, `ManageStock`, `ManageOrders`, `FulfillOrders`). The UI checks permissions, not role names, so the role-to-permission mapping exists only on the server.
- **Errors:** `401` no session, or the session expired or was ended.
- **Service:** `AccountService.GetCurrentUserAsync`

### 10.5 Session and CSRF rules

| Topic | Rule |
|---|---|
| Cookie | HttpOnly (JavaScript cannot read it), `SameSite=Strict`, `Path=/`, `Secure` in every environment except local development (`http`). Never stored in `localStorage`. |
| Lifetime | **Idle timeout** 8 hours without activity (renewed while the user is active), and an **absolute limit** of 12 hours after sign-in however active the user is; then `401` and the user signs in again. Both configurable. |
| Response headers | Every response: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`. Every `/api` response also `Cache-Control: no-store` and `Pragma: no-cache`, so nothing is cached after sign-out. |
| CSRF | `POST`/`PATCH`/`DELETE` need `X-CSRF-TOKEN`; a missing or wrong token returns `400` (verified), including on login and logout. `GET` is never state-changing. |
| CORS | Not enabled. The browser calls the API from the same address as the app (the Vite dev server proxies `/api`). |
| Error shape | `401` and `403` return the standard `application/problem+json` body, with no redirect to a login page. |
| Password storage | Salted hash from ASP.NET Core Identity's password hasher (verified format: PBKDF2 with HMAC-SHA512, 100,000 iterations, 16-byte salt). |
| Password rules | At least 12 characters, at least 5 different characters, no common fragments (`password`, `passw0rd`, `qwerty`, `letmein`, `12345678`), and not containing the part of the email before the `@`. Failures say the password is "too easy to guess" and why, and never repeat the password. |

### 10.6 When an existing session stops working

The cookie is self-contained, so the server re-checks it against the database through Identity's **security stamp**: a random value stored on each user and copied into the cookie at login. If the stored value no longer matches, the session is rejected with `401`. The check runs on **every request** by default. The interval is configurable with `Auth:SessionRevalidationSeconds` (default `0` = every request); a larger value trades immediate invalidation for fewer database reads.

These behaviors were verified against the real framework (ASP.NET Core Identity 10.0, PostgreSQL) in a throwaway experiment:

| Event | Does Identity rotate the stamp itself? | Effect on an existing session (check on every request) |
|---|---|---|
| Password changed or reset | **Yes** | Ended: next request `401` |
| Role added or removed | **No** | Not ended, but roles are re-read from the database: a removed role gives `403` on the next request |
| Account locked (lockout end set) | **No** | **Not ended**: the user stays signed in |
| Account deleted | – | Ended: `401` |
| Logout | **Yes, rotated by `AccountService` (Step 10E)** | Ended everywhere: every copy of the user's cookie returns `401` |

Because Identity does not rotate the stamp for role changes or locks, **TechStore's `AccountService` rotates it explicitly** whenever an administrator changes a user's role or locked state (`AssignRoleAsync`, `SetLockedAsync`). Those changes therefore end every existing session for that user: the next request returns `401`, and the user signs in again to pick up the new role. A password change rotates the stamp through Identity itself. With the default Identity setting (a re-check only every 30 minutes) every one of these changes took up to 30 minutes to take effect in the experiment, which is why the default here is to check on every request.

#### Logout ends every session of the user (Step 10E)

A session cookie is self-contained, so clearing it in the browser alone would leave a *copy* (for example one stolen from a compromised machine) working until it expires. This was verified before the fix: a saved cookie still returned `200` after logout. Therefore `POST /api/auth/logout` **rotates the user's security stamp** (`AccountService.RevokeSessionsAsync`) and then clears the cookie. Because the stamp is checked on every request, every copy of that user's cookie, in any browser, stops working at once (`401`).

- **Same user, other browsers:** signed out too. This is intended for an internal tool; signing in again creates a fresh session.
- **Other users:** unaffected.
- **Nobody can revoke someone else's session:** logout needs a valid session (anonymous callers get `401`) and a valid CSRF token (`400` otherwise), and both checks run before the action. A malicious page can make a browser send the cookie but cannot know the token.
- **Contract unchanged:** `204 No Content` with an expiring cookie on success, `401` without a session, `400` for a missing or wrong CSRF token.
- **Alternative if per-device logout is ever required:** a server-side session store (Identity's `ITicketStore`), which tracks each session individually but needs a session table and clean-up.

Cost of per-request checking, measured: about 5 small indexed database commands per authenticated request. That is negligible for an internal tool; the interval can be relaxed through configuration if NFR4 measurements ever show a need.

### 10.7 Initial Admin and test accounts

- **No public registration.** There is no endpoint that creates users.
- **First Admin (bootstrap):** at startup, if **no Admin exists**, the API requires `Auth:InitialAdmin:Email` and `Auth:InitialAdmin:Password` (optionally `Auth:InitialAdmin:DisplayName`), supplied as environment variables (`Auth__InitialAdmin__Password`) or, in development, `dotnet user-secrets` (stored outside the repository). They are never placed in `appsettings*.json`, source code or documents.
  - **Missing credentials and no Admin:** startup **fails** with a clear error that names the missing settings but contains no secret values: *"No administrator account exists and no initial administrator is configured. Set Auth:InitialAdmin:Email and Auth:InitialAdmin:Password (environment variables or user-secrets) and restart."*
  - **An Admin already exists:** startup succeeds with or without bootstrap credentials, and configured credentials are ignored. Bootstrap never overwrites or changes an existing user.
  - **Invalid bootstrap values** (for example a password shorter than 12 characters) also fail startup; the message states the rule that was broken, never the value.
  - The password is never written to logs, responses or exception messages. The log records only that an Admin account was created.
- **Unused bootstrap settings:** once an Admin exists, leftover `Auth:InitialAdmin:*` settings are ignored and the API logs a warning (without their values) asking you to remove them.
- **Test accounts** (one per role, with passwords generated for each test run) are described by `Auth:TestAccounts:{n}:Email|Password|Role`. They are created **only when the environment name is `Testing`**. If test accounts are configured in any other environment, startup fails instead of creating them.
- **Migrations not applied:** if the database is reachable but the authentication tables do not exist, startup fails with *"The authentication tables do not exist in the database. Apply the database migrations (dotnet ef database update --project backend/database) and restart."*
- **Unreachable database at startup:** the API still starts (so `/health` can report `503`), logs a warning that account initialization was skipped, and performs the Admin check on the next start.
- Logs record sign-in successes and failures (email and outcome only). Passwords, hashes, tokens and request bodies are never logged.

### 10.8 Configuration keys

| Key | Default | Meaning |
|---|---|---|
| `Auth:SessionMinutes` | `480` | Idle timeout (renewed while the user is active) |
| `Auth:RequireSecureCookie` | not set | Session and CSRF cookies require https. Not set = required everywhere except the Development and Testing environments. Setting it to `false` anywhere else is refused at startup unless the next setting is also `true` |
| `Auth:AllowInsecureCookiesForLocalHttp` | `false` | Explicit "this is a local-only http setup" acknowledgement (Docker Compose on a developer machine). It weakens nothing on its own |
| `Auth:TrustedProxies` | empty | Single IP addresses (no networks) of reverse proxies whose `X-Forwarded-For` is believed, only its last entry, so the login rate limit sees real clients. Empty = the header is ignored. Anything that is not an IP address stops the API from starting |
| `DataProtection:KeysPath` | not set | Folder for the cookie/CSRF key ring. Set it to a persistent volume in containers so sessions survive restarts |
| `Auth:SessionMaxHours` | `12` | Absolute session limit, counted from sign-in; must be at least 1 |
| `Auth:RateLimit:LoginPerClientPerMinute` | `10` | Sign-in attempts per minute per client address; at least 1 |
| `Auth:RateLimit:LoginGlobalPerMinute` | `100` | Sign-in attempts per minute across all clients (about 100 times what a small team needs; lower it if your team is small); at least 1 |
| `Auth:SessionRevalidationSeconds` | `0` | How often the security stamp is re-checked against the database; `0` = every request. A value above 60 logs a startup warning. |
| `Auth:InitialAdmin:Email`, `:Password`, `:DisplayName` | none | First-Admin bootstrap (section 10.7); secrets only via environment variables or user-secrets |
| `Auth:TestAccounts:{n}:Email`, `:Password`, `:Role` | none | Test accounts; honored only in the `Testing` environment |
