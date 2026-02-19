-- ============================================================
-- Migration: Add work_date column to progress_records
-- Purpose:   Enforce "1 day + 1 order_no = 1 record" via UNIQUE
-- ============================================================

BEGIN;

-- Step 1: Add work_date column (nullable first for backfill)
ALTER TABLE progress_records
    ADD COLUMN IF NOT EXISTS work_date DATE;

-- Step 2: Backfill work_date from created_at for existing data
UPDATE progress_records
   SET work_date = (created_at AT TIME ZONE 'Asia/Bangkok')::date
 WHERE work_date IS NULL;

-- Step 3: Deduplicate — keep only the latest record per (order_no, work_date)
-- Delete older duplicates before adding the UNIQUE constraint
DELETE FROM progress_records a
 USING progress_records b
 WHERE a.order_no  = b.order_no
   AND a.work_date = b.work_date
   AND a.id < b.id;          -- keep the row with the highest id (latest)

-- Step 4: Set NOT NULL after backfill + dedup
ALTER TABLE progress_records
    ALTER COLUMN work_date SET NOT NULL;

-- Step 5: Set default for new inserts
ALTER TABLE progress_records
    ALTER COLUMN work_date SET DEFAULT CURRENT_DATE;

-- Step 6: Create UNIQUE constraint (the core of this migration)
ALTER TABLE progress_records
    ADD CONSTRAINT uq_progress_order_date UNIQUE (order_no, work_date);

-- Step 7: Index for fast lookups by order_no
CREATE INDEX IF NOT EXISTS idx_progress_order_no
    ON progress_records (order_no);

COMMIT;
