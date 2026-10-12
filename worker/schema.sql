-- Likes for Workshop items. One row per (item, voter); voter is the SHA-256 of an extension's
-- submitter key, so a like needs no account and an install can carry its likes to another machine
-- by copying that key.
--
--   wrangler d1 create pa-workshop-likes
--   wrangler d1 execute pa-workshop-likes --remote --file schema.sql
CREATE TABLE IF NOT EXISTS likes (
  item_id TEXT NOT NULL,
  voter   TEXT NOT NULL,
  created INTEGER NOT NULL,
  PRIMARY KEY (item_id, voter)
) WITHOUT ROWID;

-- Reading one voter's likes is a listing by voter, which the primary key does not serve.
CREATE INDEX IF NOT EXISTS likes_by_voter ON likes (voter);
