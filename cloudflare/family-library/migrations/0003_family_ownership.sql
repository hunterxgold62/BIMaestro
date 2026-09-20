-- Existing rows intentionally remain ownerless: re-uploading cannot claim them.
ALTER TABLE shared_families ADD COLUMN origin TEXT NOT NULL DEFAULT 'unknown' CHECK(origin IN('unknown','personal','ai'));
ALTER TABLE shared_families ADD COLUMN owner_hash TEXT NOT NULL DEFAULT '';
ALTER TABLE shared_families ADD COLUMN withdrawn INTEGER NOT NULL DEFAULT 0 CHECK(withdrawn IN(0,1));
