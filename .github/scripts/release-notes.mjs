// Prints one JSON line per published item, {"tag": ..., "body": ...}, with the release notes
// the item's storage release should carry: name, author and version, the description, the
// cover and preview images (pinned to each file's last commit, as the index pins them), and a link
// to the item's folder. build-index.yml compares each body with the live release and edits
// only the ones that differ.
//
// Usage: node .github/scripts/release-notes.mjs index/v1.json
import { readFileSync } from "node:fs";

const indexPath = process.argv[2] ?? "index/v1.json";
const index = JSON.parse(readFileSync(indexPath, "utf8"));

for (const item of index.items ?? []) {
  const tag = item?.package?.release?.tag;
  if (!tag) {
    continue;
  }

  const urls = item.urls ?? {};
  const images = [];
  if (urls.cover) {
    images.push(`<img src="${urls.cover}" alt="Cover" height="180">`);
  }
  if (urls.preview) {
    images.push(`<img src="${urls.preview}" alt="Preview" width="720">`);
  }

  const lines = [`**${item.name}** by ${item.author || "unknown"}, version ${item.version}`];
  if (item.description) {
    lines.push("", item.description);
  }
  if (images.length > 0) {
    lines.push("", ...images);
  }
  if (urls.folder) {
    lines.push("", `[Item page](${urls.folder})`);
  }

  console.log(JSON.stringify({ tag, body: lines.join("\n") }));
}
