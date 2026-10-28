-- Adds fresh DRAFT orders for the reservation benchmarks (the orders that will be submitted). Run by "perf.sh drafts N [LINES]" against
-- the disposable run database only, right after "perf.sh reset" (it refuses to run twice on the same database).
--   :draft_count   how many Draft orders to add (id 1001 and up)
--   :draft_lines   lines per order (1 to 3, one unit each)
-- Every line gets its OWN (product, warehouse) stock row, so the benchmark measures a normal reservation rather than waiting on a hot
-- row, and the stock is always sufficient (only products that have at least 20 units available are used). This is the benchmark's
-- precondition; the submit request itself is what gets timed, and it goes through the real API.
\set ON_ERROR_STOP on
BEGIN;

-- psql does not expand :variables inside a $$ ... $$ block, so the values are handed over as session settings.
SELECT set_config('perf.draft_count', :'draft_count', true), set_config('perf.draft_lines', :'draft_lines', true);

DO $$
DECLARE
    n int := current_setting('perf.draft_count')::int;
    l int := current_setting('perf.draft_lines')::int;
BEGIN
    IF l NOT BETWEEN 1 AND 3 THEN RAISE EXCEPTION 'draft_lines must be 1 to 3'; END IF;
    IF n < 1 THEN RAISE EXCEPTION 'draft_count must be at least 1'; END IF;
    IF EXISTS (SELECT 1 FROM orders WHERE order_id > 1000) THEN
        RAISE EXCEPTION 'extra orders already exist: run "perf.sh reset" first';
    END IF;
END $$;

-- Per warehouse, the products it stocks with plenty of available units, in a fixed order.
CREATE TEMP TABLE perf_pool ON COMMIT DROP AS
SELECT b.warehouse_id, b.product_id, row_number() OVER (PARTITION BY b.warehouse_id ORDER BY b.product_id) - 1 AS pos,
       count(*) OVER (PARTITION BY b.warehouse_id) AS size
FROM inventory_balances b WHERE b.on_hand - b.reserved >= 20;

CREATE TEMP TABLE perf_draft ON COMMIT DROP AS
SELECT 1000 + g AS order_id, (g % 3) + 1 AS warehouse_id, 1 + ((g * 17) % 200) AS customer_id, g AS seq
FROM generate_series(1, :draft_count) g;

-- Order number s in warehouse w takes the next :draft_lines products of that warehouse's pool; it fails if the pool would wrap around.
CREATE TEMP TABLE perf_draft_lines ON COMMIT DROP AS
SELECT d.order_id, j AS line_no, p.product_id
FROM perf_draft d
CROSS JOIN generate_series(1, :draft_lines) j
JOIN perf_pool p ON p.warehouse_id = d.warehouse_id
 AND p.pos = ((d.seq - 1) / 3) * :draft_lines + (j - 1);

DO $$
BEGIN
    IF (SELECT count(*) FROM perf_draft_lines) <> (SELECT count(*) FROM perf_draft) * current_setting('perf.draft_lines')::int THEN
        RAISE EXCEPTION 'not enough distinct stocked products for % draft orders of % lines',
            current_setting('perf.draft_count'), current_setting('perf.draft_lines');
    END IF;
END $$;

INSERT INTO orders (order_id, customer_id, warehouse_id, status, created_at) OVERRIDING SYSTEM VALUE
SELECT order_id, customer_id, warehouse_id, 'Draft', timestamp '2026-03-01 08:00:00' + seq * interval '1 second' FROM perf_draft ORDER BY order_id;

INSERT INTO order_items (order_id, product_id, quantity, unit_price)
SELECT l.order_id, l.product_id, 1, p.price
FROM perf_draft_lines l JOIN products p USING (product_id) ORDER BY l.order_id, l.line_no;

SELECT setval(pg_get_serial_sequence('orders', 'order_id'), (SELECT max(order_id) FROM orders));
COMMIT;
ANALYZE orders;
ANALYZE order_items;
