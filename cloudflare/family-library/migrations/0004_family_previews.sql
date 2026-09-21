-- Apply once before deploying the Worker that serves community thumbnails.
ALTER TABLE shared_families ADD COLUMN has_preview INTEGER NOT NULL DEFAULT 0 CHECK(has_preview IN(0,1));
