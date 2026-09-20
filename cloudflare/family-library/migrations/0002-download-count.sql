-- Existing installations only. Check PRAGMA table_info before applying once.
ALTER TABLE shared_families ADD COLUMN download_count INTEGER NOT NULL DEFAULT 0 CHECK(download_count>=0);
