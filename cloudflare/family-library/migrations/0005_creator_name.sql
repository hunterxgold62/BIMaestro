-- Public display name supplied from the completed BIMaestro welcome profile.
ALTER TABLE shared_families ADD COLUMN creator_name TEXT NOT NULL DEFAULT '';
