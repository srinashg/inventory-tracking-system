-- Checks that the freshly generated template holds exactly the documented fixtures, then prints a checksum of the data. Building the
-- template twice must print the same checksum (that is the reproducibility proof). Read-only; run by perf.sh.
\set ON_ERROR_STOP on
CREATE TEMP TABLE perf_fx (name text, expected text, actual text);

INSERT INTO perf_fx SELECT 'products', '10000', count(*)::text FROM products;
INSERT INTO perf_fx SELECT 'distinct SKUs', '10000', count(DISTINCT sku)::text FROM products;
INSERT INTO perf_fx SELECT 'active products', '10000', count(*)::text FROM products WHERE is_active;
INSERT INTO perf_fx SELECT 'warehouses', '3', count(*)::text FROM warehouses;
INSERT INTO perf_fx SELECT 'customers', '200', count(*)::text FROM customers;
INSERT INTO perf_fx SELECT 'inventory balances', '20000', count(*)::text FROM inventory_balances;
INSERT INTO perf_fx SELECT 'products stocked in exactly 2 warehouses', '10000', count(*)::text
    FROM (SELECT product_id FROM inventory_balances GROUP BY product_id HAVING count(*) = 2) x;
INSERT INTO perf_fx SELECT 'orders', '1000', count(*)::text FROM orders;
INSERT INTO perf_fx SELECT 'orders in status ' || s.status, s.expected::text, count(o.order_id)::text
    FROM (VALUES ('Draft', 200), ('Reserved', 200), ('Picking', 100), ('Packed', 100), ('Shipped', 300), ('Canceled', 100)) s(status, expected)
    LEFT JOIN orders o ON o.status = s.status GROUP BY s.status, s.expected;
INSERT INTO perf_fx SELECT 'orders without lines', '0', count(*)::text FROM orders o WHERE NOT EXISTS (SELECT 1 FROM order_items i WHERE i.order_id = o.order_id);
INSERT INTO perf_fx SELECT 'order lines (units 1-3, 1-5 lines per order)', '3000', count(*)::text FROM order_items;
INSERT INTO perf_fx SELECT 'low-stock balances (available <= 5, active)', '>0', CASE WHEN count(*) > 0 THEN '>0' ELSE '0' END
    FROM inventory_balances WHERE on_hand - reserved <= 5;
INSERT INTO perf_fx SELECT 'movement types present (Receipt, Reservation, Release, Shipment)', '4', count(DISTINCT type)::text FROM inventory_movements;

SELECT name, expected, actual, CASE WHEN expected = actual THEN 'ok' ELSE 'FAIL' END AS result FROM perf_fx ORDER BY name;

-- A checksum over the content, ignoring generated row ids of movements (they depend only on insertion order, which is fixed anyway).
SELECT 'dataset checksum (md5)' AS name, md5(concat_ws('|',
    (SELECT md5(string_agg(product_id || ':' || sku || ':' || price || ':' || is_active, ',' ORDER BY product_id)) FROM products),
    (SELECT md5(string_agg(product_id || ':' || warehouse_id || ':' || on_hand || ':' || reserved, ',' ORDER BY product_id, warehouse_id)) FROM inventory_balances),
    (SELECT md5(string_agg(order_id || ':' || customer_id || ':' || warehouse_id || ':' || status || ':' || created_at, ',' ORDER BY order_id)) FROM orders),
    (SELECT md5(string_agg(order_id || ':' || product_id || ':' || quantity || ':' || unit_price, ',' ORDER BY order_id, product_id)) FROM order_items),
    (SELECT md5(string_agg(product_id || ':' || warehouse_id || ':' || coalesce(order_id, 0) || ':' || type || ':' || quantity_change || ':' || created_at,
                           ',' ORDER BY product_id, warehouse_id, created_at, type, order_id)) FROM inventory_movements)
)) AS checksum;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM perf_fx WHERE expected <> actual) THEN
        RAISE EXCEPTION 'fixture validation failed';
    END IF;
END $$;
