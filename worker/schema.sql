-- The Workshop's own statistics, which replace GitHub's release download counts.
--
--   wrangler d1 create pa-workshop-stats
--   wrangler d1 execute pa-workshop-stats --remote --file schema.sql

-- Likes: one row per (item, voter), where a voter is the SHA-256 of an extension's submitter key,
-- so a like needs no account and an install can carry its likes to another machine by copying
-- that key.
CREATE TABLE IF NOT EXISTS likes (
  item_id TEXT NOT NULL,
  voter   TEXT NOT NULL,
  created INTEGER NOT NULL,
  PRIMARY KEY (item_id, voter)
) WITHOUT ROWID;

-- Reading one voter's likes is a listing by voter, which the primary key does not serve.
CREATE INDEX IF NOT EXISTS likes_by_voter ON likes (voter);

-- Installs: a plain counter the extension adds to after an item is installed. It counts installs,
-- not package fetches, so previewing an item and retrying a download leave it alone.
CREATE TABLE IF NOT EXISTS installs (
  item_id  TEXT NOT NULL PRIMARY KEY,
  installs INTEGER NOT NULL DEFAULT 0,
  updated  INTEGER NOT NULL
) WITHOUT ROWID;
