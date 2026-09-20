CREATE TABLE map_publications (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id),
  display_name TEXT NOT NULL,
  version TEXT NOT NULL,
  feed_key TEXT NOT NULL UNIQUE,
  package_key TEXT NOT NULL UNIQUE,
  content_key TEXT NOT NULL,
  publisher_key_id TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX map_publications_created_at_index ON map_publications(created_at DESC);
