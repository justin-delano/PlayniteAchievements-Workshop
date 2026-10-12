/**
 * Likes: one row per (item, voter), where a voter is the SHA-256 of an extension's submitter key.
 * A press writes or deletes a single row, so pressing twice is idempotent rather than a double
 * count. Counts and the set of known item ids are memoized in the isolate for a short while; the
 * Cache API has no effect on a workers.dev deployment, so this is what keeps a browse burst from
 * becoming one query per row.
 */

export interface LikesEnv {
  LIKES: D1Database;
  LIKES_INDEX_URL: string;
}

/** Repository paths the Workshop index can hold, as manifest.v1.schema.json defines id. */
const ITEM_ID =
  /^(?:colors|notifications|frames|showcase-pages|sounds|bundles)\/[a-z0-9]+(?:-[a-z0-9]+)*$|^game-data\/[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/;
const VOTER = /^[a-f0-9]{64}$/;

const COUNTS_TTL_MS = 60 * 1000;
const INDEX_TTL_MS = 60 * 60 * 1000;

export interface LikeCounts {
  [itemId: string]: number;
}

let countsCache: { value: LikeCounts; at: number } | null = null;
let indexCache: { value: Set<string> | null; at: number } | null = null;

/** Every item's like count. Served from the isolate's copy while it is fresh. */
export async function readCounts(env: LikesEnv): Promise<LikeCounts> {
  if (countsCache && Date.now() - countsCache.at < COUNTS_TTL_MS) {
    return countsCache.value;
  }

  const rows = await env.LIKES.prepare("SELECT item_id, COUNT(*) AS n FROM likes GROUP BY item_id").all<{
    item_id: string;
    n: number;
  }>();
  const counts: LikeCounts = {};
  for (const row of rows.results ?? []) {
    counts[row.item_id] = Number(row.n);
  }

  countsCache = { value: counts, at: Date.now() };
  return counts;
}

/** The items this voter has liked. */
export async function readVoted(env: LikesEnv, voter: string): Promise<string[]> {
  const rows = await env.LIKES.prepare("SELECT item_id FROM likes WHERE voter = ?")
    .bind(voter)
    .all<{ item_id: string }>();
  return (rows.results ?? []).map((row) => row.item_id);
}

export async function countFor(env: LikesEnv, itemId: string): Promise<number> {
  const row = await env.LIKES.prepare("SELECT COUNT(*) AS n FROM likes WHERE item_id = ?")
    .bind(itemId)
    .first<{ n: number }>();
  return Number(row?.n ?? 0);
}

/** Adds or removes this voter's like, then drops the memoized counts so the next read is current. */
export async function setLike(env: LikesEnv, itemId: string, voter: string, liked: boolean): Promise<void> {
  if (liked) {
    await env.LIKES.prepare("INSERT OR IGNORE INTO likes (item_id, voter, created) VALUES (?, ?, ?)")
      .bind(itemId, voter, Date.now())
      .run();
  } else {
    await env.LIKES.prepare("DELETE FROM likes WHERE item_id = ? AND voter = ?").bind(itemId, voter).run();
  }

  countsCache = null;
}

export function isItemId(value: unknown): value is string {
  return typeof value === "string" && ITEM_ID.test(value);
}

export function isVoter(value: unknown): value is string {
  return typeof value === "string" && VOTER.test(value.toLowerCase());
}

/**
 * True when the Workshop index lists the item, so a mistyped or stale id cannot seed a row no
 * listing will ever show. An index that cannot be read accepts the id, rather than blocking likes
 * on an unrelated outage.
 */
export async function itemExists(env: LikesEnv, itemId: string): Promise<boolean> {
  const ids = await indexIds(env);
  return ids === null || ids.has(itemId);
}

async function indexIds(env: LikesEnv): Promise<Set<string> | null> {
  if (indexCache && Date.now() - indexCache.at < INDEX_TTL_MS) {
    return indexCache.value;
  }

  let ids: Set<string> | null = null;
  try {
    const response = await fetch(env.LIKES_INDEX_URL, { headers: { "User-Agent": "pa-workshop-worker" } });
    if (response.ok) {
      const index = (await response.json()) as { items?: { id?: string }[] };
      const found = new Set<string>();
      for (const item of index.items ?? []) {
        if (typeof item?.id === "string") {
          found.add(item.id);
        }
      }

      ids = found.size > 0 ? found : null;
    }
  } catch {
    ids = null;
  }

  indexCache = { value: ids, at: Date.now() };
  return ids;
}
