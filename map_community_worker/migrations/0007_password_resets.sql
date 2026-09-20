CREATE TABLE password_reset_codes (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  code_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);

CREATE INDEX password_reset_codes_user_id_index ON password_reset_codes(user_id);
CREATE INDEX password_reset_codes_expires_at_index ON password_reset_codes(expires_at);
