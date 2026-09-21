ALTER TABLE shared_families ADD COLUMN family_group_id TEXT NOT NULL DEFAULT '';
ALTER TABLE shared_families ADD COLUMN revision_number INTEGER NOT NULL DEFAULT 1 CHECK(revision_number>=1);
ALTER TABLE shared_families ADD COLUMN change_note TEXT NOT NULL DEFAULT '';
ALTER TABLE shared_families ADD COLUMN superseded INTEGER NOT NULL DEFAULT 0 CHECK(superseded IN(0,1));
CREATE INDEX IF NOT EXISTS idx_shared_families_group_revision ON shared_families(family_group_id, revision_number);
