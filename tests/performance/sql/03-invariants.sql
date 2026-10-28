-- Stock and order invariants (NFR1, NFR9). They hold for the fixtures and must hold after ANY run, whatever the HTTP responses said:
-- the database state is the judge. Read-only; run by perf.sh. Exits with an error if any check fails.
\set ON_ERROR_STOP on
CREATE TEMP TABLE perf_inv (name text, violations bigint);

INSERT INTO perf_inv SELECT 'balance: on_hand >= 0', count(*) FROM inventory_balances WHERE on_hand < 0;
INSERT INTO perf_inv SELECT 'balance: reserved >= 0', count(*) FROM inventory_balances WHERE reserved < 0;
INSERT INTO perf_inv SELECT 'balance: reserved <= on_hand (nothing oversold)', count(*) FROM inventory_balances WHERE reserved > on_hand;

-- Reserved stock equals the units on orders that currently hold a reservation (Reserved, Picking, Packed).
INSERT INTO perf_inv
SELECT 'reserved = units on Reserved/Picking/Packed orders', count(*) FROM (
    SELECT b.product_id, b.warehouse_id, b.reserved, coalesce(a.units, 0) AS units
    FROM inventory_balances b
    LEFT JOIN (
        SELECT i.product_id, o.warehouse_id, sum(i.quantity) AS units
        FROM order_items i JOIN orders o USING (order_id)
        WHERE o.status IN ('Reserved', 'Picking', 'Packed')
        GROUP BY i.product_id, o.warehouse_id
    ) a USING (product_id, warehouse_id)
    WHERE b.reserved <> coalesce(a.units, 0)
) x;

-- The movement history explains the balances exactly.
INSERT INTO perf_inv
SELECT 'on_hand = receipts + adjustments + shipments', count(*) FROM (
    SELECT b.product_id, b.warehouse_id
    FROM inventory_balances b
    LEFT JOIN (SELECT product_id, warehouse_id, sum(quantity_change) AS s FROM inventory_movements
               WHERE type IN ('Receipt', 'Adjustment', 'Shipment') GROUP BY 1, 2) m USING (product_id, warehouse_id)
    WHERE b.on_hand <> coalesce(m.s, 0)
) x;
INSERT INTO perf_inv
SELECT 'reserved = reservations + releases + shipments', count(*) FROM (
    SELECT b.product_id, b.warehouse_id
    FROM inventory_balances b
    LEFT JOIN (SELECT product_id, warehouse_id, sum(quantity_change) AS s FROM inventory_movements
               WHERE type IN ('Reservation', 'Release', 'Shipment') GROUP BY 1, 2) m USING (product_id, warehouse_id)
    WHERE b.reserved <> coalesce(m.s, 0)
) x;

-- Order state matches its movements: a Draft has none; a canceled or shipped order has the matching release or shipment.
INSERT INTO perf_inv
SELECT 'Draft orders have no movements', count(*) FROM inventory_movements m JOIN orders o USING (order_id) WHERE o.status = 'Draft';
INSERT INTO perf_inv
SELECT 'every Shipped order has shipment movements for all its units', count(*) FROM (
    SELECT o.order_id FROM orders o
    JOIN (SELECT order_id, sum(quantity) AS u FROM order_items GROUP BY order_id) i USING (order_id)
    LEFT JOIN (SELECT order_id, -sum(quantity_change) AS s FROM inventory_movements WHERE type = 'Shipment' GROUP BY order_id) m USING (order_id)
    WHERE o.status = 'Shipped' AND i.u <> coalesce(m.s, 0)
) x;
INSERT INTO perf_inv
SELECT 'no order is both reserved and released/shipped twice', count(*) FROM (
    SELECT order_id FROM inventory_movements WHERE order_id IS NOT NULL AND type IN ('Release', 'Shipment')
    GROUP BY order_id, product_id, type HAVING count(*) > 1
) x;

SELECT name, violations, CASE WHEN violations = 0 THEN 'ok' ELSE 'FAIL' END AS result FROM perf_inv ORDER BY name;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM perf_inv WHERE violations <> 0) THEN
        RAISE EXCEPTION 'invariant check failed';
    END IF;
END $$;
