ALTER TABLE users ADD COLUMN avatar_url TEXT;
ALTER TABLE users ADD COLUMN publisher_handle TEXT;
CREATE UNIQUE INDEX users_publisher_handle_index ON users(publisher_handle);

CREATE TABLE publish_tokens (
  token_hash TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);

CREATE INDEX publish_tokens_user_id_index ON publish_tokens(user_id);
CREATE INDEX publish_tokens_expires_at_index ON publish_tokens(expires_at);

CREATE TABLE oauth_authorization_codes (
  code_hash TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  client_id TEXT NOT NULL,
  redirect_uri TEXT NOT NULL,
  code_challenge TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);

CREATE INDEX oauth_authorization_codes_expires_at_index ON oauth_authorization_codes(expires_at);
