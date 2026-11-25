-- Apply once. Existing workers ignore this column until deployed with quota.js v2.
-- No counters, thresholds, enable flags or expiry dates are changed here.
ALTER TABLE quota_policy ADD COLUMN scope TEXT NOT NULL DEFAULT 'unverified'
 CHECK(scope IN('unverified','account'));
-- Activate scope='account' only after reconciling ALL R2 buckets and usage,
-- seeding additional reservations without lowering existing counters,
-- and wiring BOTH Workers to this same D1 database.
