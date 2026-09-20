CREATE TABLE announcements (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  category TEXT NOT NULL DEFAULT 'notice' CHECK (category IN ('update', 'tips', 'notice')),
  tag TEXT,
  summary TEXT,
  content TEXT NOT NULL,
  cover_image_url TEXT,
  author_id TEXT REFERENCES users(id),
  is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
  is_published INTEGER NOT NULL DEFAULT 1 CHECK (is_published IN (0, 1)),
  priority INTEGER NOT NULL DEFAULT 0,
  min_client_version TEXT,
  publish_at TEXT NOT NULL,
  expires_at TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE INDEX announcements_query_index ON announcements(is_published, is_pinned DESC, publish_at DESC);
CREATE INDEX announcements_category_index ON announcements(category, is_published, publish_at DESC);