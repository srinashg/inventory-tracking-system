-- Deterministic performance fixtures (NFR4): 10,000 products and 1,000 orders, plus the stock and the stock history behind them.
-- Run by tests/performance/perf.sh ONLY, against the disposable template database "techstore_perf_template" (right after the EF Core
-- migrations). Nothing here is random: the same script always produces the same data (the checksum in 02-validate-fixtures.sql proves it).
--
-- Shape of the data
--   3 warehouses, 200 customers, 10,000 active products (SKU PERF-00001 ... PERF-10000).
--   Every product is stocked in exactly two of the three warehouses (20,000 balance rows).
--   1,000 orders with 1 to 5 lines each (units 1 to 3 per line), spread over the three warehouses:
--     Draft 200, Reserved 200, Picking 100, Packed 100, Shipped 300, Canceled 100.
--   The stock and the movement history are consistent with those orders (every reservation, release and shipment has its movement),
--   so the stock invariants in 03-invariants.sql hold from the very first request.
--   (No Adjustment movements: the fixtures only need receipts, reservations, releases and shipments.)
--   Every 100th product is "low stock" (available 0 to 5) so the dashboard has alerts to show.
\set ON_ERROR_STOP on
BEGIN;

INSERT INTO warehouses (warehouse_id, name, location) OVERRIDING SYSTEM VALUE
VALUES (1, 'Perf Warehouse North', 'North'), (2, 'Perf Warehouse South', 'South'), (3, 'Perf Warehouse East', 'East');

INSERT INTO customers (customer_id, name, email) OVERRIDING SYSTEM VALUE
SELECT g, 'Perf Customer ' || lpad(g::text, 4, '0'), 'customer' || lpad(g::text, 4, '0') || '@perf.test'
FROM generate_series(1, 200) g;

INSERT INTO products (product_id, sku, name, price, is_active) OVERRIDING SYSTEM VALUE
SELECT g, 'PERF-' || lpad(g::text, 5, '0'), 'Perf Product ' || lpad(g::text, 5, '0'),
       round((5 + ((g * 37) % 99500) / 100.0)::numeric, 2), true
FROM generate_series(1, 10000) g;

-- The order lines. Product index i = product_id - 1 is stocked in warehouses (i % 3) + 1 and ((i + 1) % 3) + 1, so each order picks
-- only products that its own warehouse stocks. k is different for every line of an order (977 and 3300 share no factor).
CREATE TEMP TABLE perf_orders ON COMMIT DROP AS
SELECT g AS order_id,
       (g % 3) + 1 AS warehouse_id,
       1 + ((g * 17) % 200) AS customer_id,
       1 + ((g * 7) % 5) AS line_count,
       CASE g % 10 WHEN 1 THEN 'Draft' WHEN 2 THEN 'Draft' WHEN 3 THEN 'Reserved' WHEN 4 THEN 'Reserved'
                   WHEN 5 THEN 'Picking' WHEN 6 THEN 'Packed' WHEN 7 THEN 'Shipped' WHEN 8 THEN 'Shipped'
                   WHEN 9 THEN 'Shipped' ELSE 'Canceled' END AS status,
       timestamp '2026-02-01 08:00:00' + g * interval '5 minutes' AS created_at
FROM generate_series(1, 1000) g;

CREATE TEMP TABLE perf_lines ON COMMIT DROP AS
SELECT o.order_id, o.warehouse_id, o.status, o.created_at, j AS line_no,
       ((o.order_id * 131 + j * 977) % 3300) * 3 + ((o.warehouse_id - 1 - (j % 2) + 3) % 3) + 1 AS product_id,
       1 + ((o.order_id * 3 + j) % 3) AS qty
FROM perf_orders o CROSS JOIN LATERAL generate_series(1, o.line_count) j;

-- Stock: what each (product, warehouse) must hold at the end, and the opening receipt that makes the history add up.
CREATE TEMP TABLE perf_stock ON COMMIT DROP AS
WITH pairs AS (
    SELECT p.product_id, w.warehouse_id
    FROM generate_series(1, 10000) p(product_id)
    CROSS JOIN LATERAL (VALUES ((p.product_id - 1) % 3 + 1), (p.product_id % 3 + 1)) w(warehouse_id)
), usage AS (
    SELECT product_id, warehouse_id,
           coalesce(sum(qty) FILTER (WHERE status IN ('Reserved', 'Picking', 'Packed')), 0) AS active_reserved,
           coalesce(sum(qty) FILTER (WHERE status = 'Shipped'), 0) AS shipped
    FROM perf_lines GROUP BY product_id, warehouse_id
)
SELECT pr.product_id, pr.warehouse_id,
       coalesce(u.active_reserved, 0)::int AS reserved,
       (coalesce(u.active_reserved, 0)
         + CASE WHEN pr.product_id % 100 = 0 THEN pr.product_id % 6 ELSE 20 + (pr.product_id * 7) % 480 END)::int AS on_hand,
       coalesce(u.shipped, 0)::int AS shipped
FROM pairs pr LEFT JOIN usage u USING (product_id, warehouse_id);

INSERT INTO inventory_balances (product_id, warehouse_id, on_hand, reserved)
SELECT product_id, warehouse_id, on_hand, reserved FROM perf_stock ORDER BY product_id, warehouse_id;

INSERT INTO orders (order_id, customer_id, warehouse_id, status, created_at) OVERRIDING SYSTEM VALUE
SELECT order_id, customer_id, warehouse_id, status, created_at FROM perf_orders ORDER BY order_id;

INSERT INTO order_items (order_item_id, order_id, product_id, quantity, unit_price) OVERRIDING SYSTEM VALUE
SELECT row_number() OVER (ORDER BY l.order_id, l.line_no), l.order_id, l.product_id, l.qty, p.price
FROM perf_lines l JOIN products p USING (product_id)
ORDER BY l.order_id, l.line_no;

-- The stock history: the opening receipt, then each order's reservation, release (canceled) and shipment, in time order.
-- on_hand = receipts + adjustments + shipments, reserved = reservations + releases + shipments (see 03-invariants.sql).
INSERT INTO inventory_movements (product_id, warehouse_id, order_id, quantity_change, type, reason, created_at)
SELECT product_id, warehouse_id, order_id, quantity_change, type, NULL, created_at
FROM (
    SELECT s.product_id, s.warehouse_id, NULL::bigint AS order_id, s.on_hand + s.shipped AS quantity_change, 'Receipt' AS type,
           timestamp '2026-01-01 00:00:00' + s.product_id * interval '1 second' AS created_at
    FROM perf_stock s WHERE s.on_hand + s.shipped > 0
  UNION ALL
    SELECT l.product_id, l.warehouse_id, l.order_id, l.qty, 'Reservation', l.created_at + interval '1 minute'
    FROM perf_lines l WHERE l.status <> 'Draft'
  UNION ALL
    SELECT l.product_id, l.warehouse_id, l.order_id, -l.qty, 'Release', l.created_at + interval '2 minutes'
    FROM perf_lines l WHERE l.status = 'Canceled'
  UNION ALL
    SELECT l.product_id, l.warehouse_id, l.order_id, -l.qty, 'Shipment', l.created_at + interval '3 minutes'
    FROM perf_lines l WHERE l.status = 'Shipped'
) m
ORDER BY created_at, order_id NULLS FIRST, product_id, type;

-- New rows created later (by the API or by 04-drafts.sql) must continue after these ids.
SELECT setval(pg_get_serial_sequence('warehouses', 'warehouse_id'), (SELECT max(warehouse_id) FROM warehouses));
SELECT setval(pg_get_serial_sequence('customers', 'customer_id'), (SELECT max(customer_id) FROM customers));
SELECT setval(pg_get_serial_sequence('products', 'product_id'), (SELECT max(product_id) FROM products));
SELECT setval(pg_get_serial_sequence('orders', 'order_id'), (SELECT max(order_id) FROM orders));
SELECT setval(pg_get_serial_sequence('order_items', 'order_item_id'), (SELECT max(order_item_id) FROM order_items));

COMMIT;

-- Fresh planner statistics, so query plans in the benchmarks are the ones a settled database would use.
ANALYZE;
