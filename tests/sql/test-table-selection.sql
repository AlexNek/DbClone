-- ============================================================================
-- TABLE SELECTION TEST FIXTURE
-- Fully idempotent: safe to run any number of times.
--
-- Purpose: exercises every edge case of the DbClone table selection feature:
--   * foreign keys into excluded tables        → dangling FKs are stripped
--   * FK from an UNselected table              → scoped clean aborts (boundary)
--   * self-referencing and circular FKs
--   * views / view-on-view / materialized views depending on excluded tables
--   * partitions of an excluded parent         → orphaned partitions
--   * excluding a single partition             → boundary conflict on clean
--   * owned vs standalone sequences, triggers, RLS policies
--   * case-insensitive matching ("MixedCase" vs "mixedcase")
--   * stale exclusions (table renamed away — see scenario E below)
--
-- All objects live in schema sel_test. Everything is dropped and recreated,
-- and all row counts are DETERMINISTIC so Compare results are reproducible.
--
-- Suggested connection values for manual testing (any local/hosted PG 16+):
--   source:      Host=test.example.com  Database=srctest   (run this script)
--   destination: Host=test.example.com  Database=dsttest   (empty, or primed
--                with this script for the abort-before-destruct scenarios)
-- ============================================================================

-- ============================================================================
-- 0. IDEMPOTENT TEARDOWN
-- ============================================================================
DROP SCHEMA IF EXISTS sel_test CASCADE;
CREATE SCHEMA sel_test;
SET search_path TO sel_test, public;

-- ============================================================================
-- 1. BASE TABLES — FK chain: customers ← orders ← order_items → products
-- ============================================================================
CREATE TABLE customers (
    id         serial PRIMARY KEY,
    name       text NOT NULL,
    email      text UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE products (
    id    serial PRIMARY KEY,
    name  text NOT NULL,
    price numeric(10,2) NOT NULL CHECK (price >= 0)
);

CREATE TABLE orders (
    id          serial PRIMARY KEY,
    customer_id int  NOT NULL REFERENCES customers(id),
    total       numeric(12,2) NOT NULL DEFAULT 0,
    status      text NOT NULL DEFAULT 'new',
    created_at  timestamptz NOT NULL DEFAULT '2026-01-15'
);

CREATE TABLE order_items (
    id         serial PRIMARY KEY,
    order_id   int NOT NULL REFERENCES orders(id),
    product_id int NOT NULL REFERENCES products(id),
    qty        int NOT NULL CHECK (qty > 0)
);

-- audit_log is deliberately a "bystander": it references customers but is
-- usually left UNSELECTED. That makes it the fixture for the scoped-clean
-- abort test (scenario D): dropping customers while audit_log still points
-- at it must abort BEFORE any destructive statement.
CREATE TABLE audit_log (
    id          bigserial PRIMARY KEY,
    customer_id int REFERENCES customers(id),
    note        text
);

CREATE TABLE legacy_notes (
    id   serial PRIMARY KEY,
    note text
);

-- ============================================================================
-- 2. SELF-REFERENCING FK + CIRCULAR FK PAIR
-- ============================================================================
CREATE TABLE employees (
    id         serial PRIMARY KEY,
    name       text NOT NULL,
    manager_id int REFERENCES employees(id)
);

CREATE TABLE tbl_a (
    id   serial PRIMARY KEY,
    val  text,
    b_id int
);

CREATE TABLE tbl_b (
    id   serial PRIMARY KEY,
    val  text,
    a_id int
);

-- Circular dependency — added after both tables exist. Deferred so the
-- paired rows below can be inserted in one transaction.
ALTER TABLE tbl_a ADD CONSTRAINT fk_a_to_b
    FOREIGN KEY (b_id) REFERENCES tbl_b(id) DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE tbl_b ADD CONSTRAINT fk_b_to_a
    FOREIGN KEY (a_id) REFERENCES tbl_a(id) DEFERRABLE INITIALLY DEFERRED;

-- ============================================================================
-- 3. PARTITIONED TABLE (range) — parent + 2 bounds + default
-- ============================================================================
CREATE TABLE events (
    id         serial,
    created_at date NOT NULL,
    payload    text,
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

CREATE TABLE events_y2024 PARTITION OF events
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
CREATE TABLE events_y2025 PARTITION OF events
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE events_default PARTITION OF events DEFAULT;

-- ============================================================================
-- 4. CASE-SENSITIVITY PROBE — two tables differing only in letter case.
--    TableId matching is CASE-INSENSITIVE, so excluding one excludes BOTH
--    (known behavior — verify it in the dialog validation summary).
-- ============================================================================
CREATE TABLE "MixedCase" (
    "Id" serial PRIMARY KEY,
    val  text
);

CREATE TABLE "mixedcase" (
    "Id" serial PRIMARY KEY,
    val  text
);

-- ============================================================================
-- 5. RLS + POLICY (table-owned object — disappears with its table)
-- ============================================================================
CREATE TABLE users (
    id        serial PRIMARY KEY,
    tenant_id int NOT NULL,
    username  text NOT NULL
);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
CREATE POLICY users_tenant_isolation ON users
    USING (tenant_id = current_setting('app.current_tenant_id', true)::int);

-- ============================================================================
-- 6. SEQUENCES — one owned by a table, one standalone
-- ============================================================================
CREATE SEQUENCE standalone_sel_seq START 500 INCREMENT 5;

CREATE TABLE seq_probe (
    id_from_owned    int GENERATED ALWAYS AS IDENTITY,
    id_from_external int DEFAULT nextval('standalone_sel_seq'),
    label            text
);

-- ============================================================================
-- 7. VIEWS — direct, multi-table, transitive (view on view), materialized
-- ============================================================================
CREATE VIEW v_order_totals AS
SELECT o.id AS order_id, o.status, SUM(oi.qty * p.price) AS amount
FROM orders o
JOIN order_items oi ON oi.order_id = o.id
JOIN products p     ON p.id = oi.product_id
GROUP BY o.id, o.status;

CREATE VIEW v_customer_orders AS
SELECT c.id AS customer_id, c.name, COUNT(o.id) AS order_count
FROM customers c
LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name;

-- Transitive: depends on v_order_totals, not on any table directly.
CREATE VIEW v_big_orders AS
SELECT * FROM v_order_totals WHERE amount > 100;

CREATE MATERIALIZED VIEW mv_product_stats AS
SELECT p.id AS product_id, p.name, SUM(oi.qty) AS units_sold
FROM products p
LEFT JOIN order_items oi ON oi.product_id = p.id
GROUP BY p.id, p.name
WITH DATA;

-- ============================================================================
-- 8. FUNCTION + TRIGGERS on two different tables (table-owned objects)
-- ============================================================================
CREATE OR REPLACE FUNCTION fn_sel_touch()
RETURNS trigger AS $$
BEGIN
    NEW.created_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_customers_touch
    BEFORE UPDATE ON customers
    FOR EACH ROW EXECUTE FUNCTION fn_sel_touch();

CREATE TRIGGER trg_orders_touch
    BEFORE UPDATE ON orders
    FOR EACH ROW EXECUTE FUNCTION fn_sel_touch();

-- ============================================================================
-- 9. INDEXES (table-owned — dropped together with their table)
-- ============================================================================
CREATE INDEX idx_orders_customer  ON orders (customer_id);
CREATE INDEX idx_items_order      ON order_items (order_id);
CREATE INDEX idx_items_product    ON order_items (product_id);
CREATE INDEX idx_audit_customer   ON audit_log (customer_id);
CREATE INDEX idx_events_created   ON events (created_at);

-- ============================================================================
-- 10. DETERMINISTIC DATA (fixed row counts for reproducible Compare runs)
-- ============================================================================
INSERT INTO customers (name, email)
SELECT 'Customer ' || i, 'user' || i || '@test.example.com'
FROM generate_series(1, 10) i;                                   -- 10 rows

INSERT INTO products (name, price)
SELECT 'Product ' || i, (i * 10.5)::numeric(10,2)
FROM generate_series(1, 8) i;                                    -- 8 rows

INSERT INTO orders (customer_id, total, status)
SELECT (i % 10) + 1, i * 7.25, CASE WHEN i % 3 = 0 THEN 'paid' ELSE 'new' END
FROM generate_series(1, 30) i;                                   -- 30 rows

INSERT INTO order_items (order_id, product_id, qty)
SELECT (i % 30) + 1, (i % 8) + 1, (i % 5) + 1
FROM generate_series(1, 60) i;                                   -- 60 rows

INSERT INTO audit_log (customer_id, note)
SELECT (i % 10) + 1, 'audit entry ' || i
FROM generate_series(1, 5) i;                                    -- 5 rows

INSERT INTO legacy_notes (note) VALUES ('keep me'), ('also keep me');  -- 2

INSERT INTO employees (name, manager_id) VALUES
    ('Director', NULL),
    ('Manager A', 1),
    ('Manager B', 1),
    ('Dev 1', 2),
    ('Dev 2', 2),
    ('Dev 3', 3);                                                -- 6 rows

BEGIN;
INSERT INTO tbl_a (id, val)      VALUES (1, 'a1'), (2, 'a2'), (3, 'a3');
INSERT INTO tbl_b (id, val, a_id) VALUES (1, 'b1', 1), (2, 'b2', 2), (3, 'b3', 3);
UPDATE tbl_a SET b_id = id;      -- closes the circle: a1↔b1, a2↔b2, a3↔b3
COMMIT;                                                          -- 3 + 3 rows

INSERT INTO events (created_at, payload)
SELECT d::date, 'event ' || d::date
FROM generate_series('2024-01-01'::date, '2024-04-10'::date, '1 day'::interval) d;  -- 100 → events_y2024
INSERT INTO events (created_at, payload)
SELECT d::date, 'event ' || d::date
FROM generate_series('2025-01-01'::date, '2025-04-10'::date, '1 day'::interval) d;  -- 100 → events_y2025
INSERT INTO events (created_at, payload) VALUES ('2030-06-01', 'out of range');      -- 1 → events_default

INSERT INTO "MixedCase" (val) SELECT 'upper ' || i FROM generate_series(1, 4) i;     -- 4 rows
INSERT INTO "mixedcase" (val) SELECT 'lower ' || i FROM generate_series(1, 4) i;     -- 4 rows

INSERT INTO users (tenant_id, username)
SELECT (i % 2) + 1, 'user_' || i
FROM generate_series(1, 6) i;                                    -- 6 rows

INSERT INTO seq_probe (label) SELECT 'probe ' || i FROM generate_series(1, 3) i;     -- 3 rows

-- ============================================================================
-- TEST SCENARIOS (run against the DbClone UI with this database as SOURCE)
-- ============================================================================
-- A. Exclude sel_test.orders only
--    Expect in the dialog validation summary:
--      * dangling FK: order_items.fk order_id → orders is listed (stripped on copy)
--      * skipped views: v_order_totals, v_customer_orders, v_big_orders (transitive!)
--      * trigger trg_orders_touch goes with the table
--    After copy: destination has no orders; order_items exists WITHOUT the FK;
--    the three views are absent; everything else intact.
--
-- B. Exclude the partition PARENT sel_test.events
--    Expect: events_y2024 / events_y2025 / events_default reported as
--    orphaned partitions (skipped). Destination ends up with none of them.
--
-- C. Exclude ONLY partition sel_test.events_y2024 (parent stays selected)
--    Wrong-use case: a partition cannot be dropped while its parent remains.
--    "Replace only the selected tables" cleanup must ABORT before any DROP
--    (partition boundary conflict in the log). The copy must not run
--    partially destructive.
--
-- D. Select ONLY sel_test.customers (everything else excluded) and copy into
--    a destination primed with this same script (so audit_log exists there).
--    Wrong-use case: audit_log (unselected, stays on destination) still has
--    an FK into customers → scoped clean must ABORT before destruct and list
--    the conflict. Then try the "clear entire destination" choice: it must
--    succeed and leave ONLY customers on the destination.
--
-- E. Stale exclusion: apply scenario A, save it as a preset, then DROP
--    sel_test.legacy_notes on the source (DROP TABLE sel_test.legacy_notes;)
--    and add legacy_notes to the selection exclusion manually. The summary
--    must report it as a stale exclusion (matches no source table) and the
--    copy must proceed without it. Restore by re-running this script.
--
-- F. Case probe: exclude "MixedCase". Because matching is case-insensitive,
--    BOTH "MixedCase" and "mixedcase" are excluded — verify the count in the
--    dialog before applying.
--
-- G. Circular FKs: exclude ONLY sel_test.tbl_a. Expect the dangling FK
--    tbl_b.fk_b_to_a to be stripped; the copy completes without deadlock.
--    (Excluding BOTH tbl_a and tbl_b copies neither — clean pair.)
--
-- H. RLS: exclude sel_test.users → policy users_tenant_isolation disappears
--    with the table. Keep users but note policies are table-owned objects.
--
-- I. Sequences: exclude sel_test.seq_probe → its identity sequence goes with
--    it; standalone_sel_seq must SURVIVE (it is table-independent).
-- ============================================================================
