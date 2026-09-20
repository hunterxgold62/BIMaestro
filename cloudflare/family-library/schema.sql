-- Additive schema: does not modify the existing library_configuration record.
CREATE TABLE IF NOT EXISTS quota_policy (
 id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN(0,1)),
 threshold_percent INTEGER NOT NULL DEFAULT 80 CHECK(threshold_percent BETWEEN 1 AND 80),
 verified_until TEXT NOT NULL DEFAULT '',
 r2_bytes_limit INTEGER NOT NULL DEFAULT 10000000000,
 r2_a_limit INTEGER NOT NULL DEFAULT 1000000,
 r2_b_limit INTEGER NOT NULL DEFAULT 10000000,
 worker_requests_limit INTEGER NOT NULL DEFAULT 100000,
 d1_reads_limit INTEGER NOT NULL DEFAULT 5000000,
 d1_writes_limit INTEGER NOT NULL DEFAULT 100000,
 d1_bytes_limit INTEGER NOT NULL DEFAULT 500000000
);
CREATE TABLE IF NOT EXISTS quota_state (
 id INTEGER PRIMARY KEY CHECK(id=1), r2_bytes INTEGER NOT NULL DEFAULT 0 CHECK(r2_bytes>=0),
 r2_a INTEGER NOT NULL DEFAULT 0 CHECK(r2_a>=0), r2_b INTEGER NOT NULL DEFAULT 0 CHECK(r2_b>=0),
 worker_requests INTEGER NOT NULL DEFAULT 0 CHECK(worker_requests>=0),
 d1_reads INTEGER NOT NULL DEFAULT 0 CHECK(d1_reads>=0), d1_writes INTEGER NOT NULL DEFAULT 0 CHECK(d1_writes>=0),
 d1_bytes INTEGER NOT NULL DEFAULT 65536 CHECK(d1_bytes>=0)
);
INSERT OR IGNORE INTO quota_policy(id) VALUES(1);
INSERT OR IGNORE INTO quota_state(id) VALUES(1);
CREATE TABLE IF NOT EXISTS shared_families (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL, revit_version INTEGER NOT NULL,
 description TEXT NOT NULL, size_bytes INTEGER NOT NULL, created_at TEXT NOT NULL,
 status TEXT NOT NULL CHECK(status IN('pending','ready')),
 download_count INTEGER NOT NULL DEFAULT 0 CHECK(download_count>=0),
 origin TEXT NOT NULL DEFAULT 'unknown' CHECK(origin IN('unknown','personal','ai')),
 owner_hash TEXT NOT NULL DEFAULT '',
 withdrawn INTEGER NOT NULL DEFAULT 0 CHECK(withdrawn IN(0,1))
);
