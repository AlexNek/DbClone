-- ============================================================================
-- POSTGRESQL COMPREHENSIVE DB COPY TEST SUITE (Neon Compatible)
-- Fully idempotent: safe to run any number of times.
--
-- Version-layered tables:
--   test_all_types      → PG 16 baseline (copies to any PG >= 16)
--   test_pg17_types     → requires PG 17+ on destination (uuidv7, vector)
--   test_pg18_types     → requires PG 18+ on destination (VIRTUAL columns)
-- ============================================================================

-- ============================================================================
-- 0. IDEMPOTENT TEARDOWN
--    Publications are cluster-level (not schema-bound), so drop explicitly.
--    Everything else lives in copy_test and is removed by CASCADE.
-- ============================================================================
DROP PUBLICATION IF EXISTS test_pub;
DROP SCHEMA IF EXISTS copy_test CASCADE;

-- ============================================================================
-- 1. EXTENSIONS & SETUP
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS btree_gin;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS vector; -- Common in modern Neon stacks

CREATE SCHEMA copy_test;
SET search_path TO copy_test, public;

-- ============================================================================
-- 2. ENUMS (custom type with dependency ordering before tables)
-- ============================================================================
CREATE TYPE order_status AS ENUM ('pending', 'confirmed', 'shipped', 'delivered', 'cancelled');
CREATE TYPE priority_level AS ENUM ('low', 'medium', 'high', 'critical');

-- Simulate enum evolution: ADD VALUE after creation (common in real schemas)
ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'returned';

-- ============================================================================
-- 3. DOMAINS (constrained types — tests DDL replication + CHECK on domain)
-- ============================================================================
CREATE DOMAIN email_address AS text
    CHECK (VALUE ~* '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$');

CREATE DOMAIN positive_int AS integer
    CHECK (VALUE > 0);

CREATE DOMAIN short_code AS varchar(10)
    CHECK (VALUE = upper(VALUE));

CREATE DOMAIN percentage AS numeric(5,2)
    CHECK (VALUE >= 0 AND VALUE <= 100);

-- ============================================================================
-- 4. PG 17+ FEATURES (destination must be PG 17 or newer)
-- ============================================================================

-- 4a. uuidv7() default + vector extension type + HNSW index
CREATE TABLE test_pg17_types (
    id uuid PRIMARY KEY DEFAULT uuidv7(), -- PG 17+ built-in
    col_vector vector(3),                 -- pgvector extension
    col_json_path jsonpath,               -- PG 17 jsonpath matured
    created_at timestamptz DEFAULT now()
);

CREATE INDEX idx_pg17_vector ON test_pg17_types USING hnsw (col_vector vector_cosine_ops);

-- 4b. JSON_TABLE (PG 17+ feature, fully mature in PG 18)
CREATE TABLE test_json_source (
    id serial PRIMARY KEY,
    raw_json jsonb
);

INSERT INTO test_json_source (raw_json) VALUES 
('{"employees": [{"name": "Alice", "dept": "Engineering", "salary": 90000}, {"name": "Bob", "dept": "Sales", "salary": 75000}]}'),
('{"employees": [{"name": "Charlie", "dept": "Engineering", "salary": 95000}]}');

-- 4c. MERGE with RETURNING (PG 17+ feature)
CREATE TABLE merge_target (id int PRIMARY KEY, val text, updated_at timestamptz DEFAULT now());
CREATE TABLE merge_source (id int PRIMARY KEY, val text);

INSERT INTO merge_target (id, val) VALUES (1, 'old1'), (2, 'old2');
INSERT INTO merge_source (id, val) VALUES (2, 'new2'), (3, 'new3');

MERGE INTO merge_target t
USING merge_source s ON t.id = s.id
WHEN MATCHED THEN 
    UPDATE SET val = s.val, updated_at = now()
WHEN NOT MATCHED THEN 
    INSERT (id, val) VALUES (s.id, s.val)
RETURNING *, 'merged' AS action;

-- ============================================================================
-- 5. PG 18+ FEATURES (destination must be PG 18 or newer)
-- ============================================================================

-- 5a. VIRTUAL Generated Columns (PG 18: computed on read, not stored)
CREATE TABLE test_pg18_types (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    base_value int,
    stored_gen int GENERATED ALWAYS AS (base_value * 2) STORED,
    virtual_gen int GENERATED ALWAYS AS (base_value + 10) VIRTUAL, -- PG 18 only
    created_at timestamptz DEFAULT now()
);

-- ============================================================================
-- 6. COMPREHENSIVE DATA TYPES — PG 16 BASELINE (Serialization Stress Test)
--    All columns here exist in PG 16. Includes enum + domain columns.
-- ============================================================================
CREATE TABLE test_all_types (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), -- PG 13+ safe
    col_smallint smallint,
    col_integer integer,
    col_bigint bigint,
    col_numeric numeric(30, 10),
    col_real real,
    col_double double precision,
    col_money money,
    col_char char(10),
    col_varchar varchar(255),
    col_text text,
    col_bytea bytea,
    col_date date,
    col_time time,
    col_timetz timetz,
    col_timestamp timestamp,
    col_timestamptz timestamptz,
    col_interval interval,
    col_boolean boolean,
    col_uuid uuid,
    col_inet inet,
    col_cidr cidr,
    col_macaddr macaddr,
    col_macaddr8 macaddr8,
    col_bit bit(8),
    col_varbit varbit(64),
    col_point point,
    col_line line,
    col_lseg lseg,
    col_box box,
    col_path path,
    col_polygon polygon,
    col_circle circle,
    col_json json,
    col_jsonb jsonb,
    col_xml xml,
    col_tsvector tsvector,
    col_tsquery tsquery,
    col_int4range int4range,
    col_int8range int8range,
    col_numrange numrange,
    col_tsrange tsrange,
    col_tstzrange tstzrange,
    col_daterange daterange,
    -- Enum columns
    col_enum_status order_status,
    col_enum_priority priority_level DEFAULT 'medium',
    -- Domain columns
    col_domain_email email_address,
    col_domain_posint positive_int,
    col_domain_code short_code,
    col_domain_pct percentage
);

-- ============================================================================
-- 7. ENUM & DOMAIN DEDICATED TABLE (dependency ordering + ALTER TYPE tests)
-- ============================================================================
CREATE TABLE test_enum_domain (
    id serial PRIMARY KEY,
    status order_status NOT NULL DEFAULT 'pending',
    priority priority_level NOT NULL DEFAULT 'low',
    contact_email email_address,
    quantity positive_int NOT NULL DEFAULT 1,
    region_code short_code,
    completion percentage,
    notes text,
    created_at timestamptz DEFAULT now()
);

-- ============================================================================
-- 8. ADVANCED PARTITIONING (Tests partition metadata & bound copying)
-- ============================================================================
CREATE TABLE test_partitioned_range (
    id serial,
    created_at timestamptz NOT NULL,
    tenant_id int,
    data text,
    PRIMARY KEY (id, created_at, tenant_id)
) PARTITION BY RANGE (created_at, tenant_id);

CREATE TABLE test_part_range_p1 PARTITION OF test_partitioned_range 
    FOR VALUES FROM ('2024-01-01', 1) TO ('2025-01-01', 100);
CREATE TABLE test_part_range_p2 PARTITION OF test_partitioned_range 
    FOR VALUES FROM ('2025-01-01', 100) TO ('2026-01-01', 200);
CREATE TABLE test_part_range_def PARTITION OF test_partitioned_range DEFAULT;

CREATE TABLE test_partitioned_hash (
    id uuid PRIMARY KEY,
    payload jsonb
) PARTITION BY HASH (id);

CREATE TABLE test_part_hash_p0 PARTITION OF test_partitioned_hash FOR VALUES WITH (MODULUS 4, REMAINDER 0);
CREATE TABLE test_part_hash_p1 PARTITION OF test_partitioned_hash FOR VALUES WITH (MODULUS 4, REMAINDER 1);
CREATE TABLE test_part_hash_p2 PARTITION OF test_partitioned_hash FOR VALUES WITH (MODULUS 4, REMAINDER 2);
CREATE TABLE test_part_hash_p3 PARTITION OF test_partitioned_hash FOR VALUES WITH (MODULUS 4, REMAINDER 3);

-- ============================================================================
-- 9. SEQUENCES & IDENTITY
-- ============================================================================
CREATE SEQUENCE custom_seq START 1000 INCREMENT 5;

CREATE TABLE test_identity_generated (
    id_generated int GENERATED ALWAYS AS IDENTITY (START WITH 100 INCREMENT BY 10),
    id_custom_seq int DEFAULT nextval('custom_seq'),
    base_val int,
    stored_gen int GENERATED ALWAYS AS (base_val * 2) STORED,
    PRIMARY KEY (id_generated)
);

-- ============================================================================
-- 10. INDEXES (All types — PG 16 compatible)
-- ============================================================================
CREATE INDEX idx_btree ON test_all_types (col_integer);
CREATE INDEX idx_hash ON test_all_types USING hash (col_text);
CREATE INDEX idx_gin_jsonb ON test_all_types USING gin (col_jsonb);
CREATE INDEX idx_gist_point ON test_all_types USING gist (col_point);
CREATE INDEX idx_brin ON test_partitioned_range USING brin (created_at);
CREATE INDEX idx_spgist ON test_all_types USING spgist (col_text);
CREATE INDEX idx_covering ON test_all_types (col_integer) INCLUDE (col_text, col_boolean);
CREATE INDEX idx_partial ON test_all_types (col_text) WHERE col_boolean = true;
CREATE INDEX idx_expr ON test_all_types (lower(col_varchar));
CREATE INDEX idx_trgm ON test_all_types USING gin (col_text gin_trgm_ops);

-- Index on enum column (btree supports enum ordering)
CREATE INDEX idx_enum_status ON test_enum_domain (status);
CREATE INDEX idx_enum_priority ON test_enum_domain (priority);

-- ============================================================================
-- 11. VIEWS & MATERIALIZED VIEWS
-- ============================================================================
CREATE VIEW test_view AS 
SELECT id, col_text, col_integer, col_enum_status FROM test_all_types WHERE col_boolean = true;

CREATE MATERIALIZED VIEW test_matview AS
SELECT col_integer, count(*) as cnt FROM test_all_types GROUP BY col_integer WITH DATA;

CREATE UNIQUE INDEX idx_matview ON test_matview (col_integer);

-- View exercising domain columns
CREATE VIEW test_domain_view AS
SELECT id, status, priority, contact_email, quantity, completion
FROM test_enum_domain
WHERE status <> 'cancelled';

-- ============================================================================
-- 12. CONSTRAINTS & ROW LEVEL SECURITY (RLS)
-- ============================================================================
CREATE TABLE test_rls (
    id serial PRIMARY KEY,
    tenant_id int,
    data text
);

ALTER TABLE test_rls ADD CONSTRAINT chk_tenant CHECK (tenant_id > 0);
ALTER TABLE test_rls ENABLE ROW LEVEL SECURITY;
ALTER TABLE test_rls FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON test_rls
    USING (tenant_id = current_setting('app.current_tenant_id', true)::int);

-- ============================================================================
-- 13. FUNCTIONS & TRIGGERS
-- ============================================================================
CREATE OR REPLACE FUNCTION fn_update_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.col_timestamptz = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_update_time
BEFORE UPDATE ON test_all_types
FOR EACH ROW EXECUTE FUNCTION fn_update_timestamp();

-- Trigger using domain validation in function body
CREATE OR REPLACE FUNCTION fn_validate_order()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.quantity IS NOT NULL AND NEW.quantity < 1 THEN
        RAISE EXCEPTION 'quantity must be positive (domain: positive_int)';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_validate_order
BEFORE INSERT OR UPDATE ON test_enum_domain
FOR EACH ROW EXECUTE FUNCTION fn_validate_order();

-- ============================================================================
-- 14. LOGICAL REPLICATION (Neon Native Support)
-- ============================================================================
CREATE PUBLICATION test_pub FOR TABLE test_all_types, test_partitioned_range, test_enum_domain
    WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);

-- ============================================================================
-- 15. DATA POPULATION
-- ============================================================================
INSERT INTO test_all_types (
    col_smallint, col_integer, col_bigint, col_numeric, col_real, col_double, col_money,
    col_char, col_varchar, col_text, col_bytea, col_date, col_time, col_timetz, 
    col_timestamp, col_timestamptz, col_interval, col_boolean, col_uuid, col_inet, 
    col_cidr, col_macaddr, col_macaddr8, col_bit, col_varbit, col_point, col_line, 
    col_lseg, col_box, col_path, col_polygon, col_circle, col_json, col_jsonb, col_xml, 
    col_tsvector, col_tsquery, col_int4range, col_int8range, col_numrange, col_tsrange, 
    col_tstzrange, col_daterange,
    col_enum_status, col_enum_priority,
    col_domain_email, col_domain_posint, col_domain_code, col_domain_pct
) VALUES
(
    32767, 2147483647, 9223372036854775807, 99999999999999999999.9999999999, 3.4028235e+38, 1.7976931348623157e+308, 999999.99,
    'CHAR      ', 'VARCHAR', 'Neon PG18 test: !@#$%^&*()', E'\\xdeadbeef',
    '2026-08-01', '23:59:59', '23:59:59+05', '2026-08-01 23:59:59', '2026-08-01 23:59:59+00', '1 year 2 months',
    true, gen_random_uuid(), '192.168.1.1', '192.168.1.0/24', '08:00:2b:01:02:03', '08:00:2b:01:02:03:04:05',
    B'10101010', B'1010101010101010', '(1.5, 2.5)', '{1,2,3}', '((1,2),(3,4))', '((1,2),(3,4))', '((1,2),(3,4),(5,6))', '<(1,2),3>',
    '{"key": "value"}', '{"key": "value"}', '<root><child>text</child></root>',
    to_tsvector('english', 'quick brown fox'), to_tsquery('english', 'quick & fox'),
    '[1, 10)', '[100, 1000)', '(1.1, 9.9)', '[2026-01-01, 2026-12-31)', '[2026-01-01 00:00:00+00, 2026-12-31 23:59:59+00)', '[2026-01-01, 2026-12-31)',
    'shipped', 'high',
    'test@example.com', 42, 'ABC', 99.95
),
(
    NULL, NULL, NULL, NULL, NULL, NULL, NULL, '', '', '', NULL, 
    'infinity', '-infinity', NULL, '-infinity', 'infinity', NULL, 
    false, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 
    NULL, '{}'::jsonb, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
    NULL, NULL,
    NULL, NULL, NULL, NULL
);

-- PG 17+ table data
INSERT INTO test_pg17_types (col_vector, col_json_path) VALUES
('[0.1, 0.2, 0.3]', '$.employees[*].name'),
('[0.9, 0.8, 0.7]', '$.employees[0].salary');

INSERT INTO test_partitioned_range (created_at, tenant_id, data)
SELECT '2024-01-01'::timestamptz + (random() * (interval '2 years')), (random() * 200)::int, 'Data row ' || i
FROM generate_series(1, 5000) i;

INSERT INTO test_partitioned_hash (id, payload)
SELECT gen_random_uuid(), jsonb_build_object('index', i, 'data', md5(random()::text))
FROM generate_series(1, 2000) i;

SELECT setval('custom_seq', 5000);

INSERT INTO test_identity_generated (base_val) VALUES (10), (20), (30);
INSERT INTO test_pg18_types (base_value) VALUES (100), (200), (300);

-- Enum/domain table data (exercises all enum values + domain constraints)
INSERT INTO test_enum_domain (status, priority, contact_email, quantity, region_code, completion, notes) VALUES
('pending',    'low',      'alice@example.com',   1,  'US',  0.00,  'New order'),
('confirmed',  'medium',   'bob@example.com',     5,  'EU',  25.50, 'Payment received'),
('shipped',    'high',     'charlie@example.com', 2,  'APAC', 75.00, 'In transit'),
('delivered',  'critical', 'dave@example.com',    10, 'US',  100.00, 'Signed by recipient'),
('cancelled',  'low',      NULL,                  1,  NULL,  NULL,  'Customer cancelled'),
('returned',   'medium',   'eve@example.com',     3,  'EU',  50.00, 'Defective item');
