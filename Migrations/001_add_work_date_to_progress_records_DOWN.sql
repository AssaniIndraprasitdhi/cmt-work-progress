-- ============================================================
-- Rollback: Remove work_date from progress_records
-- ============================================================

BEGIN;

DROP INDEX IF EXISTS idx_progress_order_no;
ALTER TABLE progress_records DROP CONSTRAINT IF EXISTS uq_progress_order_date;
ALTER TABLE progress_records DROP COLUMN IF EXISTS work_date;

COMMIT;
