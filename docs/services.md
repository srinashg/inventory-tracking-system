# TechStore Services

This document defines every backend service: what it does, the step-by-step logic, the database work, the result, and the constraints its tests must cover. It is the specification for the TDD cycles in Step 6.

Related documents: `architecture.md` (user-level workflows), `requirements.md` (what must be true), `entities.md` and `diagrams/schema.dbml` (data), `api-contract.md` (HTTP endpoints that call these services).

**Request path:** `Controller → Service → DbContext → PostgreSQL → Response`

---

## 1. Service Overview

| Service (one file each in `backend/services`) | Operations | Used by endpoints |
|---|---|---|
| `ProductService` | CreateProduct, ListProducts, UpdateProduct | `POST/GET /api/products`, `PATCH /api/products/{id}` |
| `ReferenceDataService` | ListWarehouses, ListCustomers | `GET /api/warehouses`, `GET /api/customers` |
| `InventoryService` | GetInventory, ReceiveStock, AdjustStock | `GET /api/inventory`, `POST /api/inventory/receipts`, `POST /api/inventory/adjustments` |
| `OrderService` | CreateDraftOrder, GetOrder, ListOrders | `POST /api/orders`, `GET /api/orders/{id}`, `GET /api/orders` |
| `ReservationService` | SubmitOrder, CancelOrder | `POST /api/orders/{id}/submit`, `POST /api/orders/{id}/cancel` |
| `FulfillmentService` | PickOrder, PackOrder, ShipOrder | `POST /api/orders/{id}/pick`, `/pack`, `/ship` |
| `DashboardService` | GetSummary | `GET /api/dashboard/summary` |

`GET /health` is the ASP.NET Core health-check middleware, not a service. A small `DatabaseHealthCheck` confirms the database is reachable (`backend/controllers/HealthChecks`).

Submit/cancel live in `ReservationService` and pick/pack/ship in `FulfillmentService` because they share the same rule sets (stock reservation vs. status progression), which keeps each class focused.

---

## 2. Conventions Shared by All Services

### 2.1 Layers and responsibilities

- **Controller:** receives HTTP, checks the request shape (required fields, ranges), calls one service method, turns the result into an HTTP response. No queries, no business rules.
- **Service:** owns validation, business rules and transactions. It is the only layer that uses `TechStoreDbContext`. It is the **authoritative validator**: it rejects invalid input itself, so service tests do not depend on the controller.
- **DTOs:** services accept and return DTOs, defined in `backend/dto` (created in Step 6 because the services need them; Step 8 adds the controllers that use them). Entities never leave the service layer.

### 2.2 Errors raised by services

Services signal failure with three exception types. Step 8's exception-handling middleware turns them into HTTP responses.

| Exception | Meaning | HTTP |
|---|---|---|
| `ValidationFailedException` | Input is invalid on its own (blank name, quantity ≤ 0, empty item list) | `400` |
| `NotFoundException` | A referenced record does not exist | `404` |
| `ConflictException` | Input is valid but the current state forbids it (insufficient stock, wrong status, duplicate SKU) | `409` |

Any other exception is unexpected: it is logged in full and returned as a generic `500`.

### 2.3 Validation rules used across services

| Field | Rule |
|---|---|
| IDs | Positive integers |
| SKU | Trimmed and upper-cased by the service; 1–50 characters. Normalizing makes `lap-1` and `LAP-1` collide as the same SKU. |
| Name | Trimmed; 1–255 characters |
| Price | 0 to 9,999,999,999.99 with at most 2 decimal places. More than 2 decimals is rejected, not rounded. |
| Quantity (receipt, order line) | Integer from 1 to 1,000,000. The cap prevents `int` overflow in `on_hand`. |
| Adjustment change | Non-zero integer from −1,000,000 to 1,000,000; reason 1–255 characters after trimming |
| Order lines | 1 to 100 lines; one line per product |
| Paging | `page` ≥ 1 (default 1); `pageSize` 1–200 (default 50) |

### 2.4 Database access rules

- Read-only queries use `AsNoTracking()` (EF Core skips change-tracking because nothing is saved) and project straight into DTOs with `Select(...)`.
- Stock and status changes use the conditional-update pattern in section 11, inside an explicit transaction (section 12).
- Services never delete rows.

### 2.5 Logging (NFR7)

Every stock-changing operation (`ReceiveStock`, `AdjustStock`, `SubmitOrder`, `CancelOrder`, `ShipOrder`) writes one structured log entry containing the operation name, outcome (`Succeeded`, `Rejected`, `Failed`), and the order id or product/warehouse ids. Logs never contain secrets.

### 2.6 Role access

Services are role-agnostic: they never look at who the caller is. ASP.NET Core authorization policies (`ManageProducts`, `ManageStock`, `ManageOrders`, `FulfillOrders`, plus "signed in" for reads) sit in front of the controllers; the role-to-policy mapping is in `api-contract.md` §2.1. Authentication code (login, logout, users, session checks) lives in its own `AccountService`, separate from the inventory and order services above, and is described in `api-contract.md` §10.

---

## 3. ProductService

### 3.1 CreateProduct

- **Purpose:** Add a new product to the catalog.
- **Request:** An admin submits the "Add product" form → `POST /api/products` with `sku`, `name`, `price`.
- **Service:**
  1. Validate and normalize sku, name, price (section 2.3). Any failure → `ValidationFailedException`.
  2. Create a `Product` with `IsActive = true`.
  3. `Add` it to `db.Products` and call `SaveChangesAsync()`.
  4. If PostgreSQL raises a unique violation (`23505`, constraint `ux_products_sku`) → `ConflictException("SKU already exists")`.
  5. Map the saved entity (now with its generated ID) to a `ProductResponse`.
- **Database:** One `INSERT INTO products ... RETURNING product_id`. `SaveChangesAsync()` wraps it in an implicit transaction: committed on success, rolled back on the unique violation. The unique index is the final authority on duplicate SKUs, which also covers two simultaneous creates (no check-then-insert race).
- **Result:** `ProductResponse` (productId, sku, name, price, isActive) → controller returns `201 Created`.
- **Testing constraints:**
  - *Happy:* valid product saved, ID generated, `IsActive` true, SKU normalized (`" lap-1 "` → `LAP-1`).
  - *Boundary:* SKU of 1 and 50 chars OK, 51 rejected; name 255 OK, 256 rejected; price `0` and `9999999999.99` OK, `10000000000.00` rejected.
  - *Invalid:* blank SKU/name, negative price, price with 3 decimals → `ValidationFailedException`; nothing saved.
  - *Exception:* duplicate SKU (including different case) → `ConflictException`; row count unchanged. Two concurrent creates with one SKU → exactly one succeeds.

### 3.2 ListProducts

- **Purpose:** Let employees browse products and pick them for orders.
- **Request:** Products page or order form loads → `GET /api/products?activeOnly=&page=&pageSize=`.
- **Service:**
  1. Validate paging (2.3).
  2. Build the query on `db.Products` with `AsNoTracking()`; add `Where(p => p.IsActive)` when `activeOnly=true`.
  3. Count matching rows, then take one page ordered by `Sku`.
  4. Project to `ProductResponse`.
- **Database:** `SELECT COUNT(*) FROM products WHERE ...` and `SELECT ... FROM products WHERE ... ORDER BY sku LIMIT @size OFFSET @skip`. The `sku` unique index serves the ordering. No transaction needed. Paging keeps responses small with 10,000 products (NFR4).
- **Result:** `PagedResponse<ProductResponse>` (items, page, pageSize, totalCount) → `200 OK`.
- **Testing constraints:**
  - *Happy:* returns products ordered by SKU; `activeOnly=true` hides inactive ones; inactive products appear when the filter is off.
  - *Boundary:* empty table → empty list, `totalCount` 0; last partial page; page beyond the end → empty `items`; `pageSize` 1 and 200 OK.
  - *Invalid:* `page` 0, `pageSize` 0 or 201 → `ValidationFailedException`.

### 3.3 UpdateProduct

- **Purpose:** Edit product details or deactivate/reactivate a product.
- **Request:** Admin edits a product → `PATCH /api/products/{id}` with any of `sku`, `name`, `price`, `isActive`.
- **Service:**
  1. Reject the request if no field is supplied (`ValidationFailedException`); validate each supplied field.
  2. Load the product by ID; if missing → `NotFoundException`.
  3. Apply only the supplied fields.
  4. `SaveChangesAsync()`; unique violation on SKU → `ConflictException`.
  5. Return the updated `ProductResponse`.
- **Database:** `SELECT ... FROM products WHERE product_id = @id`, then `UPDATE products SET ... WHERE product_id = @id` for the changed columns only. Existing `order_items.unit_price` values are untouched, so past orders keep their price. Deactivating does not alter stock or existing orders.
- **Result:** `ProductResponse` → `200 OK`.
- **Testing constraints:**
  - *Happy:* each field updated alone and together; deactivate then reactivate; setting the same values again succeeds.
  - *Boundary:* same limits as CreateProduct; changing a SKU to its own current value is not a conflict.
  - *Invalid:* empty body, blank name, negative price → `ValidationFailedException`.
  - *Exception:* unknown ID → `NotFoundException`; SKU belonging to another product → `ConflictException`, original unchanged.
  - *State:* an existing order's `unit_price` is unchanged after a price edit.

---

## 4. ReferenceDataService

### 4.1 ListWarehouses / 4.2 ListCustomers

- **Purpose:** Supply the choices shown on order and inventory forms.
- **Request:** A form loads → `GET /api/warehouses` or `GET /api/customers`.
- **Service:** Query the table with `AsNoTracking()`, order by name, project to `WarehouseResponse` (warehouseId, name, location) or `CustomerResponse` (customerId, name, email). No paging: both lists are small in the first version.
- **Database:** `SELECT ... FROM warehouses ORDER BY name` / `SELECT ... FROM customers ORDER BY name`. No transaction.
- **Result:** List of responses → `200 OK`. An empty list is a valid result.
- **Testing constraints:**
  - *Happy:* all rows returned, ordered by name.
  - *Boundary:* empty table → empty list.
- **Note:** Creating warehouses and customers is out of scope for the first version; they are seeded as sample data.

---

## 5. InventoryService

### 5.1 GetInventory

- **Purpose:** Show on-hand, reserved and available stock for a warehouse.
- **Request:** Employee opens the inventory page → `GET /api/inventory?warehouseId=1&page=&pageSize=`.
- **Service:**
  1. Validate `warehouseId` (required) and paging.
  2. Confirm the warehouse exists; otherwise `NotFoundException`.
  3. Query `inventory_balances` joined to `products` for that warehouse, ordered by SKU, one page.
  4. Project each row to `InventoryItemResponse`, with `Available = OnHand − Reserved` calculated.
- **Database:** `SELECT EXISTS(...)` on warehouses; `SELECT COUNT(*)`; then `SELECT b.product_id, p.sku, p.name, b.on_hand, b.reserved FROM inventory_balances b JOIN products p ON ... WHERE b.warehouse_id = @w ORDER BY p.sku LIMIT/OFFSET`. Uses `ix_inventory_balances_warehouse_id`. Read-only, no explicit transaction. `Available` is never stored: it is computed so it can never disagree with the other two numbers.
- **Result:** `PagedResponse<InventoryItemResponse>` → `200 OK`.
- **Testing constraints:**
  - *Happy:* after receiving 10 and reserving 2, row shows 10 / 2 / 8.
  - *Boundary:* warehouse with no balances → empty list; paging edges as in 3.2; available 0 when fully reserved.
  - *Invalid:* missing or non-positive `warehouseId` → `ValidationFailedException`.
  - *Exception:* unknown warehouse → `NotFoundException`.

### 5.2 ReceiveStock

- **Purpose:** Increase on-hand stock when goods arrive and record it.
- **Request:** Warehouse employee submits "Receive stock" → `POST /api/inventory/receipts` with `productId`, `warehouseId`, `quantity`, optional `note`.
- **Service:**
  1. Validate quantity (1–1,000,000) and IDs.
  2. Begin a transaction.
  3. Confirm the product and warehouse exist (`NotFoundException` if not). Inactive products may still be received.
  4. Upsert the balance: add `quantity` to `on_hand`, creating the row with `reserved = 0` on first receipt.
  5. Insert an `InventoryMovement` (`Receipt`, `+quantity`, reason = note).
  6. Commit. Read the resulting balance and return it.
- **Database:** `SELECT EXISTS` on products and warehouses, then
  `INSERT INTO inventory_balances (product_id, warehouse_id, on_hand, reserved) VALUES (...) ON CONFLICT (product_id, warehouse_id) DO UPDATE SET on_hand = inventory_balances.on_hand + @qty RETURNING on_hand, reserved`,
  then `INSERT INTO inventory_movements`. Both writes share one transaction (`COMMIT`), so the movement always matches the balance. `ON CONFLICT` makes two simultaneous first receipts add up instead of one failing on the primary key.
- **Result:** `StockChangeResponse` (movementId, productId, warehouseId, onHand, reserved, available) → `201 Created`.
- **Testing constraints:**
  - *Happy:* first receipt of 10 creates balance 10/0/10 plus one `Receipt` movement; a second receipt of 5 gives 15/0/15.
  - *Boundary:* quantity 1 and 1,000,000 OK; 0, −1 and 1,000,001 rejected; receiving when on-hand is near `int.MaxValue` makes PostgreSQL raise "integer out of range" (SQLSTATE `22003`); the service rolls back and returns `ConflictException`, leaving the balance unchanged.
  - *Invalid:* missing IDs, quantity 0 → `ValidationFailedException`; no rows changed.
  - *Exception:* unknown product or warehouse → `NotFoundException`; a forced failure on the movement insert rolls back the balance change (0 partial writes).
  - *Concurrency:* 50 concurrent first receipts of 1 unit → on-hand 50, 50 movements.

### 5.3 AdjustStock

- **Purpose:** Correct on-hand stock (damage, recount) with a mandatory reason.
- **Request:** Employee submits "Correct stock" → `POST /api/inventory/adjustments` with `productId`, `warehouseId`, `quantityChange` (positive or negative), `reason`.
- **Service:**
  1. Validate: change is non-zero and within range; reason is not blank.
  2. Begin a transaction.
  3. Conditionally update the balance: `on_hand = on_hand + @change` only if the result is not below `reserved` (this also keeps it non-negative).
  4. If 0 rows changed, work out why: product/warehouse missing or no balance row → `NotFoundException`; otherwise → `ConflictException("Adjustment would drop on-hand below reserved stock")`.
  5. Insert an `InventoryMovement` (`Adjustment`, signed change, reason).
  6. Commit and return the new balance.
- **Database:** `UPDATE inventory_balances SET on_hand = on_hand + @change WHERE product_id = @p AND warehouse_id = @w AND on_hand + @change >= reserved`, then `INSERT INTO inventory_movements`. Same transaction. The `CHECK` constraints are the backstop. An adjustment needs an existing balance; use a receipt to create one.
- **Result:** `StockChangeResponse` → `201 Created`.
- **Testing constraints:**
  - *Happy:* +3 and −3 on 10/0/10; movement records the signed change and reason.
  - *Boundary:* reducing on-hand to exactly equal reserved succeeds; one below reserved → `ConflictException`; to exactly 0 with reserved 0 succeeds.
  - *Invalid:* change 0, blank or whitespace reason, reason over 255 chars → `ValidationFailedException`.
  - *Exception:* unknown product/warehouse or no balance → `NotFoundException`; conflict leaves balance and movements unchanged.
  - *Concurrency:* two simultaneous −8 adjustments on 10 on-hand → exactly one succeeds.

---

## 6. OrderService

### 6.1 CreateDraftOrder

- **Purpose:** Record what a customer wants without touching stock.
- **Request:** Sales employee saves a new order → `POST /api/orders` with `customerId`, `warehouseId`, `items[{productId, quantity}]`.
- **Service:**
  1. Validate shape: 1–100 items, each quantity 1–1,000,000, no repeated `productId` (`ValidationFailedException`).
  2. Check that the customer and warehouse exist, and load all referenced products in one query; anything missing → `NotFoundException` (message names which one).
  3. Any inactive product → `ConflictException` (message lists the SKUs).
  4. Build an `Order` (`Status = Draft`) with one `OrderItem` per line, copying each product's current `Price` into `UnitPrice`.
  5. `SaveChangesAsync()`: its implicit transaction inserts the order and all lines together or not at all.
  6. Re-read the order and return it with `totalUnits` and `totalAmount`.
- **Database:** `SELECT` on customers, warehouses and `products WHERE product_id IN (...)`; then `INSERT INTO orders`, followed by `INSERT INTO order_items` for each line, in a single `SaveChangesAsync()` transaction. **No stock table is read or written**, and no movement is logged. The `ux_order_items_order_product` and `quantity > 0` constraints back up the validation.
- **Result:** `OrderResponse` (orderId, customerId, warehouseId, status, createdAt, totalUnits, totalAmount, items[productId, sku, name, quantity, unitPrice]) → `201 Created` with a `Location` header.
- **Testing constraints:**
  - *Happy:* the 2-laptop order saves as `Draft`; unit price equals the product price; inventory identical before and after.
  - *Boundary:* 1 line and 100 lines OK, 0 and 101 rejected; quantity 1 and 1,000,000 OK, 0 and 1,000,001 rejected.
  - *Invalid:* duplicate product lines, negative quantity, missing customer/warehouse → `ValidationFailedException`.
  - *Exception:* unknown customer, warehouse or product → `NotFoundException`; inactive product → `ConflictException`; in every failure no `orders` or `order_items` row exists (all-or-nothing).
  - *State:* a later price change does not alter the saved `unitPrice`.

### 6.2 GetOrder

- **Purpose:** Show an order's details and current status.
- **Request:** Employee opens an order → `GET /api/orders/{id}`.
- **Service:** Query `db.Orders` with `AsNoTracking()` and its items and products by ID; if missing → `NotFoundException`; project to `OrderResponse`.
- **Database:** One `SELECT` joining `orders`, `order_items` and `products` (EF Core's `Include`/projection), no transaction.
- **Result:** `OrderResponse` → `200 OK`.
- **Testing constraints:**
  - *Happy:* returns all items and the current status after each transition.
  - *Exception:* unknown ID → `NotFoundException`.
  - *Boundary:* an order with the maximum 100 lines is returned in full.


### 6.3 ListOrders

- **Purpose:** Let every employee see the orders in the system, for example the whole list, or the Fulfillment queue (Reserved, Picking, Packed).
- **Request:** An orders list or the fulfillment queue loads → `GET /api/orders?status=&customerId=&warehouseId=&page=&pageSize=`.
- **Service:**
  1. Validate paging (2.3), `customerId` and `warehouseId` when supplied (positive), and every `status` value (must name an `OrderStatus`, case-insensitive; numbers are not accepted). Problems → `ValidationFailedException`.
  2. Build the query on `db.Orders` with `AsNoTracking()`; add `Where` for each supplied filter (several statuses are OR-ed, different filters are AND-ed).
  3. Count the matching rows, then take one page ordered by `OrderId` descending.
  4. Project each order to an `OrderListItem`, with `TotalUnits` and `TotalAmount` summed from its lines.
- **Database:** `SELECT COUNT(*) FROM orders WHERE ...`, then `SELECT o.*, (SELECT SUM(quantity) ...), (SELECT SUM(quantity * unit_price) ...) FROM orders o WHERE ... ORDER BY order_id DESC LIMIT @size OFFSET @skip`. The totals are computed in the same statement, so there is one query per page and no query per order. `order_id` is an ever-increasing identity, so "newest first" needs no new index; `ix_orders_status`, `ix_orders_customer_id` and `ix_orders_warehouse_id` serve the filters. Read-only, no transaction.
- **Result:** `PagedResponse<OrderListItem>` → `200 OK`. No matches is an empty page, not an error.
- **Testing constraints:**
  - *Happy:* orders come back newest first with the right ids, status, timestamp, units and amount; a status filter, several statuses, `customerId` and `warehouseId` each narrow the list, and filters combine.
  - *Boundary:* empty table → empty page with `totalCount` 0; `pageSize` 1 and 200 accepted; a partial last page; a page past the end → empty `items` with the full `totalCount`.
  - *Invalid:* unknown status (including a number or blank), `customerId` or `warehouseId` ≤ 0, `page` 0, `pageSize` 0 or 201 → `ValidationFailedException`.
  - *State:* listing changes no data; an order's status in the list follows each transition (Draft → Reserved → … → Shipped).

---

## 7. ReservationService

### 7.1 SubmitOrder

- **Purpose:** Reserve stock for every line of a Draft order, all-or-nothing.
- **Request:** Employee clicks "Submit order" → `POST /api/orders/{id}/submit`.
- **Service:**
  1. Begin a transaction.
  2. **Claim the order:** conditionally update status `Draft → Reserved`. 0 rows → load the order: missing → `NotFoundException`; any other status → `ConflictException("Order is not in Draft status")`.
  3. Load the order's items sorted by `product_id` (fixed lock order prevents deadlocks).
  4. For each item, conditionally increase `reserved` if enough stock is available. 0 rows → roll back everything and raise `ConflictException` naming the SKU, the requested quantity and the available quantity (read after the rollback).
  5. Insert one `Reservation` movement per item (`+quantity`, `order_id` set).
  6. Commit. Return the updated order.
- **Database:**
  `UPDATE orders SET status = 'Reserved' WHERE order_id = @id AND status = 'Draft'`;
  `SELECT ... FROM order_items WHERE order_id = @id ORDER BY product_id`;
  per item `UPDATE inventory_balances SET reserved = reserved + @q WHERE product_id = @p AND warehouse_id = @w AND on_hand - reserved >= @q`;
  per item `INSERT INTO inventory_movements`. One transaction: either every line is reserved and the status is `Reserved`, or `ROLLBACK` leaves everything as it was (order still `Draft`). A product that has no balance row in the warehouse matches 0 rows and is reported as insufficient stock (available 0). Competing submits are serialized by the row lock (section 11).
- **Result:** `OrderResponse` with status `Reserved` → `200 OK`.
- **Testing constraints:**
  - *Happy:* 10/0/10, submit a 2-unit order → 10/2/8, status `Reserved`, one `Reservation` movement.
  - *Boundary:* ordering exactly the available quantity succeeds (available becomes 0); one more than available fails.
  - *Exception:* unknown order → `NotFoundException`; insufficient stock → `ConflictException`; multi-line order where only the last line lacks stock → no line reserved, status `Draft`, no movements; no balance row for a product → `ConflictException`.
  - *State:* submitting an already `Reserved`, `Picking`, `Packed`, `Shipped` or `Canceled` order → `ConflictException`; submitting twice reserves only once.
  - *Concurrency:* 100 concurrent submits of 1-unit orders against 10 units → exactly 10 succeed, 90 get `ConflictException`, final balance 10 on hand / 10 reserved.
  - *Reliability:* forced failure after the first line's update leaves reserved and status unchanged.

### 7.2 CancelOrder

- **Purpose:** Cancel a Draft or Reserved order and give back any reserved stock.
- **Request:** Employee clicks "Cancel order" → `POST /api/orders/{id}/cancel`.
- **Service:**
  1. Begin a transaction.
  2. Read the order's current status. Missing → `NotFoundException`. Not `Draft` or `Reserved` → `ConflictException`.
  3. **Claim the order:** conditionally update status to `Canceled` only if it still equals the status just read. 0 rows (another request changed it first) → `ConflictException`.
  4. If it was `Reserved`: for each item (sorted by `product_id`) decrease `reserved` and insert a `Release` movement (`−quantity`).
  5. Commit. Return the updated order.
- **Database:**
  `UPDATE orders SET status = 'Canceled' WHERE order_id = @id AND status = @seen`;
  for reserved orders per item `UPDATE inventory_balances SET reserved = reserved - @q WHERE product_id = @p AND warehouse_id = @w AND reserved >= @q` and `INSERT INTO inventory_movements`. Draft cancellation changes only the `orders` row. Same transaction, so the status and the release are inseparable.
- **Result:** `OrderResponse` with status `Canceled` → `200 OK`.
- **Testing constraints:**
  - *Happy:* canceling a `Draft` leaves stock identical; canceling a `Reserved` 2-unit order takes 10/2/8 back to 10/0/10 with a `Release` movement.
  - *Exception:* unknown order → `NotFoundException`.
  - *State:* `Picking`, `Packed`, `Shipped` and already-`Canceled` orders → `ConflictException` with stock and status unchanged; canceling twice releases only once.
  - *Concurrency:* cancel racing with ship on a `Packed` order cannot both succeed (cancel is refused for `Packed`); two simultaneous cancels of a `Reserved` order → one succeeds, reserved decreases only once.
  - *Reliability:* forced failure while releasing leaves the order `Reserved` and stock unchanged.

---

## 8. FulfillmentService

### 8.1 PickOrder / 8.2 PackOrder

- **Purpose:** Record that staff started picking, then finished packing, a reserved order.
- **Request:** Warehouse employee clicks "Pick" or "Pack" → `POST /api/orders/{id}/pick` or `/pack`.
- **Service:**
  1. Conditionally update status: `Reserved → Picking` (pick) or `Picking → Packed` (pack).
  2. 0 rows → load the order: missing → `NotFoundException`; any other status → `ConflictException`.
  3. Return the updated order.
- **Database:** `UPDATE orders SET status = @next WHERE order_id = @id AND status = @expected`. A single statement is atomic by itself, so no explicit transaction is needed. Stock tables are not touched: units stay reserved.
- **Result:** `OrderResponse` → `200 OK`.
- **Testing constraints:**
  - *Happy:* `Reserved → Picking → Packed`; stock identical (10/2/8) at each step.
  - *Exception:* unknown order → `NotFoundException`.
  - *State (full transition table in section 10):* pick on `Draft`, `Picking`, `Packed`, `Shipped`, `Canceled` → `ConflictException`; pack on `Draft`, `Reserved`, `Packed`, `Shipped`, `Canceled` → `ConflictException`.
  - *Concurrency:* two simultaneous picks → one succeeds, one `ConflictException`.

### 8.3 ShipOrder

- **Purpose:** Confirm that a packed order left the warehouse and deduct the stock.
- **Request:** Warehouse employee clicks "Ship" → `POST /api/orders/{id}/ship`.
- **Service:**
  1. Begin a transaction.
  2. **Claim the order:** conditionally update status `Packed → Shipped`. 0 rows → load the order: missing → `NotFoundException`; any other status (including already `Shipped`) → `ConflictException`.
  3. Load items sorted by `product_id`.
  4. For each item, decrease both `on_hand` and `reserved` by the quantity.
  5. Insert one `Shipment` movement per item (`−quantity`, `order_id` set).
  6. Commit. Return the updated order.
- **Database:**
  `UPDATE orders SET status = 'Shipped' WHERE order_id = @id AND status = 'Packed'`;
  per item `UPDATE inventory_balances SET on_hand = on_hand - @q, reserved = reserved - @q WHERE product_id = @p AND warehouse_id = @w AND reserved >= @q AND on_hand >= @q`;
  per item `INSERT INTO inventory_movements`. One transaction. Because the status claim happens first, a repeated or racing ship request gets 0 rows and stops **before** touching stock, so stock can never be deducted twice. If an item update matches 0 rows (data inconsistency), the transaction rolls back and the failure is logged as an error.
- **Result:** `OrderResponse` with status `Shipped` → `200 OK`.
- **Testing constraints:**
  - *Happy:* 10/2/8 → 8/0/8, status `Shipped`, one `Shipment` movement per line.
  - *Boundary:* shipping an order that reserves all stock (10/10/0) → 0/0/0 OK.
  - *Exception:* unknown order → `NotFoundException`.
  - *State:* ship on `Draft`, `Reserved`, `Picking`, `Shipped`, `Canceled` → `ConflictException`, stock unchanged; shipping twice deducts once.
  - *Concurrency:* 20 simultaneous ship requests → exactly one succeeds, on-hand reduced once.
  - *Reliability:* forced failure on the second line's update rolls back the first line, the movements and the status.

---

## 9. DashboardService

### 9.1 GetSummary

- **Purpose:** Give a one-screen picture of stock and orders.
- **Request:** Employee opens the dashboard → `GET /api/dashboard/summary`.
- **Service:**
  1. Total available units: sum of `on_hand − reserved` over all balances.
  2. Low-stock items: active products whose balance in a warehouse has `available ≤ 5` (the `LowStockThreshold`, a named constant of 5), lowest first, at most 50 returned, plus the total count of such items.
  3. Order counts grouped by status; **open orders** = `Draft + Reserved + Picking + Packed`; **shipped orders** = `Shipped`.
  4. Combine into one `DashboardSummaryResponse`.
- **Database:** Three read-only queries: `SELECT COALESCE(SUM(on_hand - reserved), 0) FROM inventory_balances`; the low-stock `SELECT ... JOIN products WHERE is_active AND on_hand - reserved <= 5 ORDER BY ... LIMIT 50` with a matching `COUNT`; and `SELECT status, COUNT(*) FROM orders GROUP BY status` using `ix_orders_status`. No explicit transaction: the numbers may differ by a moment between queries, which is acceptable for a summary.
- **Result:** `DashboardSummaryResponse` (totalAvailableUnits as a 64-bit number, lowStockThreshold, lowStockCount, lowStockItems[], openOrders, shippedOrders) → `200 OK`.
- **Testing constraints:**
  - *Happy:* totals equal values computed directly from the test data.
  - *Boundary:* empty database → zeros and empty list; available exactly 5 is low stock, 6 is not; inactive products excluded from low stock; more than 50 low items → 50 returned, `lowStockCount` is the full count.
  - *State:* canceled orders are in neither open nor shipped; an order moving `Packed → Shipped` shifts one from open to shipped.

---

## 10. Order Status Transition Table

Used by `ReservationService` and `FulfillmentService`. A transition not listed is refused with `ConflictException`.

| From \ Action | submit | pick | pack | ship | cancel |
|---|---|---|---|---|---|
| **Draft** | → Reserved | ✗ | ✗ | ✗ | → Canceled |
| **Reserved** | ✗ | → Picking | ✗ | ✗ | → Canceled |
| **Picking** | ✗ | ✗ | → Packed | ✗ | ✗ |
| **Packed** | ✗ | ✗ | ✗ | → Shipped | ✗ |
| **Shipped** | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Canceled** | ✗ | ✗ | ✗ | ✗ | ✗ |

Every ✗ cell and every ✓ cell gets a test (NFR6): 30 cells.

---

## 11. Key Concepts

| Concept | What it means here |
|---|---|
| **Transaction** | A group of database operations that succeed together (`COMMIT`) or are all undone (`ROLLBACK`). No other connection sees half-finished work. |
| **EF Core and transactions** | `SaveChangesAsync()` wraps all pending changes in one implicit transaction. When a service needs several statements (or raw/conditional updates) to be atomic, it opens an explicit one with `Database.BeginTransactionAsync()` and calls `CommitAsync()` at the end. Disposing the transaction without committing rolls it back. |
| **Isolation level** | PostgreSQL's default, **Read Committed**, is used. Each statement sees only committed data. Correctness under concurrency comes from the row locks that `UPDATE` takes plus the rules below, not from a stricter isolation level. |
| **Row lock** | An `UPDATE` locks the rows it changes until the transaction ends. A second transaction updating the same row **waits**, then re-checks its `WHERE` condition against the newly committed values. |
| **Conditional update** | An `UPDATE` whose `WHERE` clause contains the business rule (for example `WHERE on_hand - reserved >= @qty`). If the rule fails, 0 rows change and the service treats it as a conflict. The check and the change are one atomic step, so there is no gap for another request to slip into. |

### Concurrency Strategy (Preventing Overselling)

**Problem:** two requests read "10 available", both decide to reserve 8, and both succeed, leaving 16 reserved against 10 units.

**Strategy: check and change in a single conditional `UPDATE`, inside a transaction, backed by database constraints.**

1. **Never read-then-write stock in application code.** The service does not `SELECT` the balance, compare in C#, then `UPDATE`. It sends the rule to the database:

   ```sql
   UPDATE inventory_balances
   SET    reserved = reserved + @qty
   WHERE  product_id = @product AND warehouse_id = @warehouse
     AND  on_hand - reserved >= @qty;      -- 1 row = reserved, 0 rows = insufficient stock
   ```

   In EF Core this is `ExecuteUpdateAsync(...)` with the same `Where` condition. It returns the number of rows changed.

2. **Row locking serializes competitors.** The first transaction locks the balance row. A competing request waits, then re-evaluates `on_hand - reserved >= @qty` against the updated value and fails cleanly (0 rows) when the stock is gone.

3. **Constraints are the safety net.** Even if a coding mistake skips the check, the `CHECK` constraints (`on_hand >= 0`, `reserved >= 0`, `reserved <= on_hand`) make PostgreSQL reject the write. A database error rolls back the transaction, and stock stays valid.

4. **Lock rows in a consistent order.** Orders with several lines process them sorted by `product_id`. Two orders that need the same products then lock them in the same sequence, so they cannot deadlock each other. If PostgreSQL still reports a deadlock (`40P01`) or serialization failure, the whole operation is rolled back and reported as a retryable conflict.

5. **Guard status changes the same way.** Moving an order to the next state is a conditional update:

   ```sql
   UPDATE orders SET status = 'Shipped'
   WHERE  order_id = @id AND status = 'Packed';   -- 0 rows = already shipped, canceled, or wrong state
   ```

   Whoever wins the row gets to run the rest of the transaction. A duplicate or racing request gets 0 rows and returns `409 Conflict` **before touching stock**, which is what prevents a second shipment from deducting twice.

6. **Unique constraints settle creation races.** `products.sku` and the `inventory_balances` primary key `(product_id, warehouse_id)` stop duplicates even when two requests arrive together. A unique-violation error (SQLSTATE `23505`) is translated into `409 Conflict`. First receipt for a product–warehouse pair uses `INSERT ... ON CONFLICT (product_id, warehouse_id) DO UPDATE SET on_hand = inventory_balances.on_hand + @qty`, so concurrent first receipts add up instead of failing.

**Expected result:** 100 concurrent submits for the same 10 units produce successful reservations that total at most 10, and every other request gets `409`. This is verified in Step 6 with PostgreSQL integration tests.

---

## 12. Transaction Boundaries

Each operation below runs inside **one** transaction. "Rollback" means every earlier statement in that transaction is undone, so stock, movements and order status stay as they were.

| Operation | Transaction contents (in order) | Commits when | Rolls back when |
|---|---|---|---|
| **Create product** | `INSERT products` | `SaveChanges` succeeds | SKU already exists (`23505`) → `409` |
| **Edit / deactivate product** | `UPDATE products` | `SaveChanges` succeeds | SKU clash → `409`; unknown id → `404` (nothing written) |
| **Receive stock** | `INSERT ... ON CONFLICT DO UPDATE` on `inventory_balances` (on_hand + qty); `INSERT inventory_movements` (`Receipt`) | Both statements succeed | Either fails (for example an invalid product or warehouse FK) |
| **Adjust stock** | Conditional `UPDATE inventory_balances` (on_hand + delta, `WHERE on_hand + delta >= reserved`); `INSERT inventory_movements` (`Adjustment`, with reason) | 1 row updated and movement inserted | 0 rows (would drop below reserved or negative) → `409` |
| **Create draft order** | Read product prices; `INSERT orders`; `INSERT order_items` (unit price copied) | Order and all items saved | Any item invalid → whole order discarded. **No stock rows are touched.** |
| **Submit (reserve)** | 1. Claim: `UPDATE orders` `Draft → Reserved` `WHERE status = 'Draft'`<br>2. Read the order's items, sorted by `product_id`<br>3. For each item: conditional `UPDATE inventory_balances` (reserved + qty)<br>4. For each item: `INSERT inventory_movements` (`Reservation`) | Every line reserved | Step 1 gives 0 rows → `409` (wrong state); any line in step 3 gives 0 rows → **roll back all lines**, `409` insufficient stock, order stays `Draft` |
| **Pick / Pack** | Conditional `UPDATE orders` (`Reserved → Picking`, `Picking → Packed`) | 1 row updated | 0 rows → `409` |
| **Ship** | 1. Claim: `UPDATE orders` `Packed → Shipped` `WHERE status = 'Packed'`<br>2. For each item (sorted): `UPDATE inventory_balances` (on_hand − qty, reserved − qty)<br>3. For each item: `INSERT inventory_movements` (`Shipment`) | All lines deducted | Step 1 gives 0 rows → `409` (already shipped or wrong state); a constraint violation in step 2 → full rollback |
| **Cancel** | 1. Read the order's current status (`Draft` or `Reserved`; any other status → `409`)<br>2. Claim: `UPDATE orders` → `Canceled` `WHERE status = <status just read>`<br>3. If it was `Reserved`: for each item (sorted) `UPDATE inventory_balances` (reserved − qty) and `INSERT inventory_movements` (`Release`) | Order canceled and any reservation released | Status is not cancelable, or the claim gives 0 rows (another request changed it first) → `409`; any later failure → full rollback |
| **Read operations** (list, view, inventory, dashboard) | Single `SELECT` queries, no explicit transaction. EF Core `AsNoTracking()` is used because nothing is saved. | n/a | n/a |

### Rules that apply to every write operation

- **All-or-nothing.** If any statement fails or throws, the transaction is disposed without `CommitAsync()` and PostgreSQL rolls everything back.
- **Movement with change.** A balance change and its `inventory_movements` row are always in the same transaction, so the audit trail can never disagree with the balances.
- **Order status with change.** An order's status and the stock effect of that status change are always in the same transaction.
- **No partial responses.** The service returns a result (or throws a domain exception) only after commit or rollback finishes, so the controller never reports success for work that was rolled back.
- **Error mapping.** Domain failures become `404` (missing), `409` (state or stock conflict), or `400` (invalid input). Unique-constraint violations become `409`. Unexpected exceptions become `500` with a generic message, and details go to the log only.

---

## 13. Constraint Summary (Where Each Rule Is Enforced)

| Rule | Database | Service |
|---|---|---|
| Unique SKU | `UNIQUE (products.sku)` | Friendly `409` message; SKU normalized to upper case |
| One balance per product–warehouse | Composite PK on `inventory_balances` | Receipt uses `ON CONFLICT` |
| Non-negative stock, available ≥ 0 | `CHECK` constraints on `inventory_balances` | Conditional updates return `409` before a violation occurs |
| Positive order quantity | `CHECK (quantity > 0)` | `400` validation |
| One line per product per order | `UNIQUE (order_id, product_id)` | `400` validation |
| Money precision | `numeric(12,2)` | Validate: reject more than 2 decimals or out of range (`400`) |
| Valid status values | `CHECK` on `orders.status` | Transition table in section 10 |
| Allowed status transitions | Not in the database | Service + conditional `UPDATE ... WHERE status = expected` |
| Adjustment needs a reason | `CHECK` on `inventory_movements` | `400` validation |
| Price frozen at order time | `order_items.unit_price` column | Copied from the product when the draft is created |
| No deletes | `ON DELETE RESTRICT` on every foreign key | Products are deactivated, orders are canceled |

---

## 14. Testing Approach (applies to Step 6)

- **One test file per service** under `tests/`, for example `ProductServiceTests`, `InventoryServiceTests`, `ReservationServiceTests`.
- **Real PostgreSQL, not the EF in-memory provider.** The rules depend on conditional `UPDATE`s, row locks, `CHECK` constraints and transactions, which the in-memory provider does not implement. Tests use a separate `techstore_test` database in the local container (created automatically if missing, migrated once per run, every table truncated before each test). Set `TECHSTORE_TEST_DB` to use another server. The connection pool is capped at 50, so the 100-request concurrency test runs with at most 50 requests holding a connection at the same moment while the rest queue.
- **Categories per service:** happy path, boundary and limit, invalid input, exception and error, state and transition, and (where noted) concurrency and forced-failure reliability tests.
- **Concurrency tests** use separate `DbContext` instances running in parallel tasks, because one `DbContext` is not thread-safe.
- **Forced-failure tests** create a temporary PostgreSQL trigger that raises an error when a movement of a given type (and optionally product) is inserted, which fails the transaction part-way through; the test then asserts that the balances, movements and order status are all unchanged. The same trigger raises SQLSTATE `40P01` to test the deadlock-to-retryable-conflict translation.
- **Order of work:** Red → Green → Refactor one behavior at a time, in the order of the numbered sections above, starting with `ProductService`.

---

## 15. Authentication Services (Step 10)

Authentication code lives in `backend/auth`, separate from the inventory and order services above. It uses ASP.NET Core Identity (`UserManager`, `SignInManager`); there is no custom password hashing or token handling. Endpoints: `api-contract.md` §10.

### 15.1 AccountService

| Operation | Purpose | Notes |
|---|---|---|
| `ValidateCredentialsAsync(email, password)` | Check a sign-in attempt | Looks the user up by email, verifies the password hash and counts failures. Wrong password, unknown email and locked account all throw the same `InvalidCredentialsException`, and an unknown email does the same hashing work as a wrong password so timing reveals nothing. The 5th consecutive failure locks the account for 15 minutes; success resets the count. |
| `LoginAsync(email, password)` | Sign in | Validates credentials, then issues the session cookie. Returns the `CurrentUser` (roles and permissions). |
| `LogoutAsync(principal)` | Sign out | Rotates the signed-in user's security stamp (`RevokeSessionsAsync`), which ends every session of that user in every browser, then clears this browser's cookie. |
| `GetCurrentUserAsync(principal)` | "Who am I" | Loads the user and roles; permissions are the policies the user satisfies, evaluated by ASP.NET Core's `IAuthorizationService`, so the role-to-permission mapping exists in one place. |
| `CreateUserAsync(email, displayName, password, role)` | Create an employee | Used by the bootstrap and test seeding; no endpoint calls it. Enforces the password policy and unique email. |
| `AssignRoleAsync(userId, role)` | Change a user's role | Replaces the role and **rotates the security stamp**, ending the user's sessions. |
| `SetLockedAsync(userId, locked)` | Lock or unlock an account | Sets or clears the lockout end and **rotates the security stamp**, ending the user's sessions. |

- **Database:** reads and updates `users`, `roles` and `user_roles` through Identity's stores and `TechStoreDbContext`. `AssignRoleAsync` and `SetLockedAsync` each make their changes and the stamp rotation as separate Identity operations; a failure between them leaves the user's sessions valid until the next successful call, so callers treat a failure as "not applied" and retry.
- **Testing constraints:** valid and invalid credentials (wrong password, unknown email, blank values, locked account all indistinguishable), lockout after exactly 5 failures and its reset on success, password policy boundary (11 vs 12 characters), duplicate email, unknown role, stamp changes after `AssignRoleAsync` and `SetLockedAsync`, no stamp change for a no-op.

- **Hardening (Step 10F):** `WeakPasswordValidator` (registered with Identity, so it applies to every password set) rejects passwords with fewer than 5 different characters, with a common fragment (`password`, `passw0rd`, `qwerty`, `letmein`, `12345678`), or containing the email name; its message never echoes the password. Cross-cutting rules live in the API pipeline, not in the service: the 12-hour absolute session limit (cookie validation), login rate limiting (`429`) and the response headers. Testing constraints: each weak-password rule and the 11-character / 12-character boundary; a strong password is accepted.

### 15.2 AuthInitializer (runs once at startup)

1. Make sure the three roles exist (idempotent).
2. If test accounts are configured: allowed only when the environment is `Testing`, otherwise fail startup. Create the missing ones.
3. If no user in the `Admin` role exists: create one from `Auth:InitialAdmin:*`; if those are missing or invalid, fail startup with a message that names settings but never values. If an Admin already exists, do nothing (credentials, if present, are ignored).
4. If the database cannot be reached, log a warning and continue (the health check reports it); nothing is created. If it is reachable but the migrations have not been applied, fail startup with a message that says so.

- **Warnings (never containing secret values):** `Auth:SessionRevalidationSeconds` above 60; `Auth:InitialAdmin:*` still configured although an Admin already exists.
- **Testing constraints:** roles created once and not duplicated; no Admin and no credentials fails with a message free of secrets; Admin present and no credentials succeeds; bootstrap creates exactly one Admin whose password verifies but never appears in any log; repeated runs and existing users are left untouched; weak bootstrap password fails; test accounts created only in `Testing` and rejected in `Development` and `Production`.

---

## 16. Startup, Database Initialization and Operations (Step 11)

These are not request-time services: they are the jobs that run when the containerized application starts. The container layout is in `architecture.md` §8; commands are in `README.md`.

### 16.1 Order of events on `docker compose up`

1. **`db`** starts. On the very first start PostgreSQL creates the database from `POSTGRES_DB` with the owner `POSTGRES_USER` and the password `POSTGRES_PASSWORD`. Later starts reuse the `pgdata` volume. Health: `pg_isready`.
2. **`migrate`** (one-shot, as the database owner) runs two scripts with `ON_ERROR_STOP`:
   1. `migrate.sql`, generated at image build by `dotnet ef migrations script --idempotent`. Each migration is wrapped in a check of `__EFMigrationsHistory`, so only missing migrations run. It creates the schema on an empty database, applies newer migrations to an existing one, and changes nothing otherwise. It never contains `DROP`, `TRUNCATE` or `DELETE FROM` (a stack test enforces this, so a destructive migration gets a human review before it can run automatically).
   2. `backend/database/Provisioning/app-role.sql`: creates the role `techstore_app` if missing; always re-applies `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS` and the password from `TECHSTORE_APP_DB_PASSWORD` (stopping with an error if that is empty); grants `CONNECT`, schema `USAGE`, `SELECT/INSERT/UPDATE` on all tables, `USAGE/SELECT/UPDATE` on all sequences, and `DELETE` on `user_roles` only; sets default privileges for tables and sequences created later by the same owner; revokes write access on `__EFMigrationsHistory`. Safe to repeat; touches no data.
   If either script fails, `migrate` exits non-zero and `api` does not start.
3. **`api`** starts and connects as `techstore_app`. `AuthInitializer` runs (§15.2): roles, then the first Admin if none exists. Health: `GET /health` (checks the database connection through the limited user).
4. **`web`** starts when `api` is healthy.

### 16.2 Restarts and recovery

- A restart of any container, or `docker compose down` followed by `up` (volumes kept), repeats step 2 harmlessly, keeps all records, and keeps sessions valid because the Data Protection key ring is on the `keys` volume.
- `docker compose down -v` deletes `pgdata` and `keys`. Recovery is a restore from a backup (`README.md` → "Backup and restore"): `pg_restore --clean --if-exists --single-transaction --exit-on-error`, then `migrate` again to re-apply newer migrations and the limited user's grants and password.
- The backup is a PostgreSQL custom-format archive of the database. It contains business data, accounts and password hashes, and not the database users' passwords (those belong to the server, not the data).

### 16.3 Configuration read at startup

`Auth:RequireSecureCookie`, `Auth:AllowInsecureCookiesForLocalHttp`, `Auth:TrustedProxies` and `DataProtection:KeysPath` (all in `api-contract.md` §10.8), plus the rate-limit and session settings from §15. Invalid values stop the API from starting with a message that names the setting. Outside Development and Testing, an insecure cookie setting without the explicit local-only acknowledgement is refused, and with it the API logs a warning.

### 16.4 Testing constraints

Idempotence (repeated `migrate` runs leave every table fingerprint unchanged); an empty database gets the full schema (a database at an older migration gets only the missing ones, by design of the idempotent EF script; not separately tested); the limited role cannot create, alter, drop or truncate, cannot use superuser features, cannot delete business records or write the migration history, and can complete the full workflow; a table added by a later migration is usable without another grant; a new `TECHSTORE_APP_DB_PASSWORD` replaces the old hash; a missing password stops provisioning without changing anything; restore is all-or-nothing and reproduces every table and account.
Tests: `LeastPrivilegeDatabaseTests` (xUnit, own database), `tests/containers/compose-stack.sh`, `compose-e2e.sh`, `backup-restore.sh`.
