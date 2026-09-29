-- Anonymous submissions have no account; preserve all existing feedback and foreign keys.
CREATE TABLE feedbacks_new (
  id TEXT PRIMARY KEY,
  user_id TEXT REFERENCES users(id),
  description TEXT NOT NULL,
  client_version TEXT,
  client_ip TEXT,
  has_logs INTEGER NOT NULL DEFAULT 0,
  logs_key TEXT,
  logs_size INTEGER DEFAULT 0,
  has_diagnostics INTEGER NOT NULL DEFAULT 0,
  diagnostics_key TEXT,
  diagnostics_size INTEGER DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'open',
  created_at TEXT NOT NULL
);
INSERT INTO feedbacks_new SELECT * FROM feedbacks;
DROP TABLE feedbacks;
ALTER TABLE feedbacks_new RENAME TO feedbacks;
CREATE INDEX feedbacks_created_at_index ON feedbacks(created_at DESC);
CREATE INDEX feedbacks_user_id_index ON feedbacks(user_id);
