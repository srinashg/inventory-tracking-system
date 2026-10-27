# TechStore Entities

An **entity** is a core object the system stores and manages. Each becomes a C# class in the backend and a table in PostgreSQL. This document defines what each entity is for, the key information it holds, how it relates to the others, and which workflow steps use it.

Exact column types, constraints and indexes are defined in Step 3 (`docs/diagrams/schema.dbml`). Field lists here are conceptual.

---

## 1. Entity Overview

| Entity | One-line role |
|---|---|
| **Product** | A sellable item in the catalog (e.g., a laptop). |
| **Warehouse** | A physical place where stock is kept. |
| **InventoryBalance** | The current stock numbers (on hand, reserved) for one product in one warehouse. |
| **InventoryMovement** | A permanent log entry of every change to stock. |
| **Customer** | The person or company an order is for. |
| **Order** | A request to ship products to a customer from one warehouse; carries the status. |
| **OrderItem** | One product line within an order, with quantity and the price at order time. |

## 2. Relationships

```
Customer 1 ──── N Order 1 ──── N OrderItem N ──── 1 Product
                   │                                  │
                   N                                  │ 1
                   │                                  │
               Warehouse 1 ──── N InventoryBalance N ─┘
                   │
                   │ 1
                   N
            InventoryMovement N ──── 1 Product
```

| Relationship | Cardinality | Meaning |
|---|---|---|
| Customer → Order | 1 : N | A customer can have many orders; an order belongs to one customer. |
| Warehouse → Order | 1 : N | An order is fulfilled from exactly one warehouse. |
| Order → OrderItem | 1 : N | An order has one or more items; an item belongs to one order. |
| Product → OrderItem | 1 : N | A product can appear in many order items. |
| Product → InventoryBalance | 1 : N | A product has one balance per warehouse that stocks it. |
| Warehouse → InventoryBalance | 1 : N | A warehouse holds a balance for each product it stocks. |
| Product ↔ Warehouse | N : N | Resolved through **InventoryBalance** (one row per product–warehouse pair). |
| Product → InventoryMovement | 1 : N | A product's stock history is many movements. |
| Warehouse → InventoryMovement | 1 : N | Movements happen in one warehouse. |
| Order ↔ Product | N : N | Resolved through **OrderItem**. |

---

## 3. Entity Definitions

### Product

- **Role:** The catalog. Defines what can be stocked and sold.
- **Key information:** ID, SKU (unique), name, price, active/inactive flag.
- **Rules:** SKU is unique. Deactivating keeps the record so past orders stay valid. The current price can change, but existing orders keep the price they were created with (see OrderItem).
- **Involved in:** Product setup, receiving stock, adjusting stock, drafting orders, reservation, shipping, cancellation, dashboard.

### Warehouse

- **Role:** Identifies where stock is stored and which location fulfills an order.
- **Key information:** ID, name, location.
- **Rules:** An order uses exactly one warehouse.
- **Involved in:** Viewing inventory, receiving stock, adjusting stock, drafting orders, reservation, shipping, cancellation.

### InventoryBalance

- **Role:** The live stock counts. This is the entity that must never oversell.
- **Key information:** product, warehouse, **OnHand** (physically present), **Reserved** (promised to submitted orders). **Available** is calculated: `OnHand − Reserved`.
- **Rules:**
  - Exactly one balance per product–warehouse pair (the pair is its composite primary key).
  - OnHand and Reserved are never negative, and Reserved never exceeds OnHand.
  - Reserving increases Reserved; shipping decreases both OnHand and Reserved; cancelling a reserved order decreases Reserved.
  - Changes happen inside database transactions with concurrency protection (designed in Steps 3 and 5).
- **Involved in:** Viewing inventory, receiving stock, adjusting stock, submit (reserve), ship, cancel (release), dashboard.

### InventoryMovement

- **Role:** An append-only audit trail: *why* and *when* stock changed. Balances show the current state; movements explain how it got there.
- **Key information:** ID, product, warehouse, quantity change (positive or negative), type (`Receipt`, `Adjustment`, `Reservation`, `Release`, `Shipment`), reason, timestamp, and a reference to the related order where one exists.
- **Quantity change meaning:** it is the signed change to the quantity the type affects: on hand for `Receipt`, `Adjustment` and `Shipment`; reserved for `Reservation` (+) and `Release` (−).
- **Rules:** Rows are only added, never edited or deleted. Written in the same transaction as the balance change they describe.
- **Involved in:** Receiving stock, adjusting stock, submit (reserve), ship, cancel (release).

### Customer

- **Role:** Who an order is for. Sample customers are used in the first version.
- **Key information:** ID, name, email.
- **Involved in:** Listing customers, drafting orders, viewing orders.

### Order

- **Role:** The central business object. It tracks the order through its lifecycle and ties together the customer, warehouse and items.
- **Key information:** ID, customer, warehouse, **status** (`Draft`, `Reserved`, `Picking`, `Packed`, `Shipped`, `Canceled`), created timestamp.
- **Rules:**
  - Status changes follow only the allowed transitions in `architecture.md` §2.
  - Status changes happen in the same transaction as the stock changes they cause.
  - Shipped and Canceled orders are final.
- **Involved in:** Drafting, viewing, submit, pick, pack, ship, cancel, dashboard.
- **Naming note:** `Order` is a reserved word in SQL. The C# class is `Order`; Step 3 will choose a safe table name.

### OrderItem

- **Role:** One product line on an order.
- **Key information:** ID, order, product, **quantity**, **unit price at order time**.
- **Rules:** Quantity is positive. A product appears at most once per order. Unit price is copied from the product when the order is created, so later price changes do not alter existing orders.
- **Involved in:** Drafting, viewing, submit (what to reserve), ship (what to deduct), cancel (what to release).

---

## 4. Entities by Workflow Step

R = read, W = write (create or update).

| Step | Product | Warehouse | InventoryBalance | InventoryMovement | Customer | Order | OrderItem |
|---|---|---|---|---|---|---|---|
| Add / edit / deactivate product | W | | | | | | |
| List products, warehouses, customers | R | R | | | R | | |
| View warehouse stock | R | R | R | | | | |
| Receive stock | R | R | W | W | | | |
| Adjust stock | R | R | W | W | | | |
| Draft an order | R | R | | | R | W | W |
| Open an order | R | R | | | R | R | R |
| Submit (reserve) | | | W | W | | W | R |
| Pick / Pack | | | | | | W | |
| Ship | | | W | W | | W | R |
| Cancel | | | W (if reserved) | W (if reserved) | | W | R |
| Dashboard | R | | R | | | R | R |

---

## 5. Authentication Entities (Step 10)

Users and roles support access control only. They have no foreign keys to the inventory and order entities in sections 1-4 (orders and stock movements do not yet record who acted).

| Entity | Role | Key information |
|---|---|---|
| **User** (`AppUser`, table `users`) | An employee who can sign in | ID, email (also the sign-in name, unique), display name, **password hash** (never the password), **security stamp**, lockout end, failed-attempt count |
| **Role** (`AppRole`, table `roles`) | A named set of permissions: `Admin`, `Sales`, `Warehouse` | ID, name |
| **UserRole** (table `user_roles`) | Gives a user a role | user ID, role ID |

- **Relationships:** User 1 : N UserRole N : 1 Role (many-to-many; the first version assigns each employee exactly one role).
- **Rules:** emails are unique (compared case-insensitively); passwords are at least 12 characters and stored only as salted hashes; the security stamp changes when a password changes and whenever an administrator changes a role or locked state, which ends that user's sessions.
- **Standard Identity tables:** ASP.NET Core Identity also requires `user_claims`, `user_logins`, `user_tokens` and `role_claims`. They stay empty in this application.
- **Involved in:** sign in, sign out, "who am I" (`GET /api/auth/me`), and the authorization check in front of every protected endpoint.

<!-- CI verification: documentation-only change, not to be merged -->
