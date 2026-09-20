CREATE TABLE feedbacks (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id),
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

CREATE INDEX feedbacks_created_at_index ON feedbacks(created_at DESC);
CREATE INDEX feedbacks_user_id_index ON feedbacks(user_id);
