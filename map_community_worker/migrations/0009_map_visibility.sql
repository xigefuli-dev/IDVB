ALTER TABLE map_publications ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0;
CREATE INDEX map_publications_is_hidden_index ON map_publications(is_hidden);
