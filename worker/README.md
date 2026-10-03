# Workshop Worker

The Cloudflare Worker that lets the extension submit from inside Playnite in one click.
It never validates or stores content itself: it hands the extension presigned upload URLs for a temporary R2 bucket, then opens the same submission issue a person would fill in by hand, with a presigned download link in the Package URL field.
The repository's intake workflow does everything after that.

## Endpoints

All responses are JSON. Errors are `{ "error": "message" }` with a 4xx or 5xx status.

`POST /v1/uploads`
Body: `{ "fileName": "better-icons.pa", "sizeBytes": 183400000, "sha256": "…", "contentType": "application/zip" }`.
Returns `{ "uploadId", "key", "partSize", "parts": [{ "partNumber", "url" }], "multipart": true }` or, for small files, `{ "key", "url", "multipart": false }`.
The extension PUTs each part (or the whole file) to the given URLs, in order, and keeps the `ETag` response headers.

`POST /v1/uploads/complete`
Body: `{ "uploadId", "key", "parts": [{ "partNumber", "etag" }] }` for multipart; no-op for single PUTs.
Returns `{ "key", "sizeBytes" }` after confirming the object exists.

`POST /v1/submissions`
Body:
```json
{
  "kind": "Per-game custom data",
  "name": "Better icons",
  "author": "someone",
  "description": "…",
  "tags": ["icons"],
  "license": "CC-BY-4.0",
  "existingId": null,
  "remove": false,
  "readme": "# Better icons\n…",
  "submitterHash": "<sha256 hex of the extension's submitter key>",
  "packageKey": "<key from /v1/uploads>",
  "previewKey": "<optional key of an uploaded preview image>"
}
```
Returns `{ "issueNumber", "issueUrl" }`. For a removal, `packageKey` is omitted and `existingId` and `remove` are set.

`GET /v1/submissions/{issueNumber}`
Returns `{ "state": "validating" | "needs-changes" | "in-review" | "published" | "closed", "message", "pullRequestUrl", "issueUrl" }` read from the issue's labels and the intake workflow's last comment.

## Setup

1. Create the bucket and the KV namespace, and give the bucket its lifecycle rules:
   ```
   wrangler r2 bucket create pa-workshop-uploads
   wrangler r2 bucket lifecycle add pa-workshop-uploads --expire-days 1 --name expire-uploads
   wrangler r2 bucket lifecycle add pa-workshop-uploads --abort-multipart-days 1 --name abort-stale-parts
   wrangler kv namespace create RATE
   ```
   Paste the KV id and your account id into `wrangler.toml`.
2. Create an R2 API token (Object Read & Write, scoped to the bucket) and a fine-grained GitHub PAT for the Workshop repository with Issues: Read and write.
   ```
   wrangler secret put R2_ACCESS_KEY_ID
   wrangler secret put R2_SECRET_ACCESS_KEY
   wrangler secret put GITHUB_TOKEN
   ```
3. In the Workshop repository, set the Actions variable `WORKSHOP_BOT_LOGIN` to the GitHub login the PAT belongs to, so intake records Playnite submitters by hash instead of by that login.
4. `npm install` then `npm run deploy`. The printed `*.workers.dev` URL goes into the extension's Workshop settings.

## Limits and cost

The bucket is temporary storage only, and three things keep it inside R2's free tier (10 GB-month, no egress fees):

- Before issuing upload URLs the Worker sums the bucket's current bytes and refuses when the new file would push it past `STORAGE_CAP_BYTES` (8 GiB by default). Every byte enters through the Worker, so this is a hard ceiling.
- Objects expire after one day and stale multipart uploads are aborted after one day (lifecycle rules above). Intake reads a package within minutes of the issue opening.
- A single package is capped at `MAX_PACKAGE_BYTES` (1 GiB).

Per IP, `DAILY_SUBMISSIONS_PER_IP` submissions a day (KV counter). Everything else is enforced by the intake workflow and by maintainer review of every pull request. Workers free tier is 100,000 requests a day; a submission uses a handful.
