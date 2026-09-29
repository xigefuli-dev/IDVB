CREATE TABLE software_versions (
  version TEXT PRIMARY KEY,
  enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
  message TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL,
  updated_by TEXT NOT NULL
);
CREATE TABLE software_version_audit (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  version TEXT NOT NULL,
  enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
  message TEXT NOT NULL,
  changed_at TEXT NOT NULL,
  changed_by TEXT NOT NULL
);
INSERT INTO software_versions VALUES ('1.6.6', 1, '', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'migration');
