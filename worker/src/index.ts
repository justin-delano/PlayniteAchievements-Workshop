import { GitHubClient } from "./github";
import { R2Signer } from "./r2";

export interface Env {
  GITHUB_REPO: string;
  GITHUB_TOKEN: string;
  R2_ACCOUNT_ID: string;
  R2_ACCESS_KEY_ID: string;
  R2_SECRET_ACCESS_KEY: string;
  R2_BUCKET: string;
  DAILY_SUBMISSIONS_PER_IP: string;
  MAX_PACKAGE_BYTES: string;
  STORAGE_CAP_BYTES: string;
  PART_SIZE_BYTES: string;
  UPLOADS: R2Bucket;
  RATE: KVNamespace;
}

// Presigned URLs live long enough for a slow upload and for the intake workflow to fetch the
// package after the issue is opened. Both are well under R2's 7-day maximum.
const UPLOAD_URL_SECONDS = 6 * 60 * 60;
const DOWNLOAD_URL_SECONDS = 3 * 24 * 60 * 60;
const SINGLE_PUT_MAX_BYTES = 64 * 1024 * 1024;

const KINDS = new Set([
  "Colors",
  "Notifications",
  "Frames",
  "Showcase page",
  "Sounds",
  "Bundle",
  "Per-game custom data",
]);
const LICENSES = new Set(["CC-BY-4.0", "CC0-1.0"]);

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    try {
      if (request.method === "OPTIONS") {
        return cors(new Response(null, { status: 204 }));
      }

      if (request.method === "POST" && url.pathname === "/v1/uploads") {
        return cors(await beginUpload(request, env));
      }
      if (request.method === "POST" && url.pathname === "/v1/uploads/complete") {
        return cors(await completeUpload(request, env));
      }
      if (request.method === "POST" && url.pathname === "/v1/submissions") {
        return cors(await createSubmission(request, env));
      }
      const status = /^\/v1\/submissions\/(\d+)$/.exec(url.pathname);
      if (request.method === "GET" && status) {
        return cors(await getSubmission(Number(status[1]), env));
      }
      if (request.method === "GET" && url.pathname === "/v1/health") {
        return cors(json({ ok: true }));
      }

      return cors(error(404, "Not found."));
    } catch (e) {
      const message = e instanceof HttpError ? e.message : "The Workshop service hit an unexpected error.";
      const status = e instanceof HttpError ? e.status : 500;
      if (!(e instanceof HttpError)) {
        console.error(e);
      }
      return cors(error(status, message));
    }
  },
};

// ---- uploads ---------------------------------------------------------------------------------

interface BeginUploadRequest {
  fileName?: string;
  sizeBytes?: number;
  sha256?: string;
  contentType?: string;
}

async function beginUpload(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(request, env, "upload");
  const body = (await readJson(request)) as BeginUploadRequest;
  const size = Number(body.sizeBytes);
  const max = Number(env.MAX_PACKAGE_BYTES);
  if (!Number.isFinite(size) || size <= 0) {
    throw new HttpError(400, "sizeBytes is required.");
  }
  if (size > max) {
    throw new HttpError(413, `The file is larger than the ${Math.floor(max / (1024 * 1024))} MB limit.`);
  }

  const safeName = sanitizeFileName(body.fileName ?? "package.zip");
  const key = `${new Date().toISOString().slice(0, 10)}/${crypto.randomUUID()}/${safeName}`;
  const signer = makeSigner(env);
  const contentType = body.contentType ?? "application/octet-stream";

  // The storage quota is enforced here, at the only place bytes can enter the bucket, so the
  // bucket can never grow past the free tier regardless of how many uploads are in flight.
  const cap = Number(env.STORAGE_CAP_BYTES);
  if (Number.isFinite(cap) && cap > 0) {
    const used = await signer.totalBytes();
    if (used + size > cap) {
      throw new HttpError(507, "The Workshop's upload storage is full right now. Try again in a few hours, when pending uploads have been processed.");
    }
  }

  if (size <= SINGLE_PUT_MAX_BYTES) {
    const putUrl = await signer.presignPut(key, UPLOAD_URL_SECONDS);
    return json({ key, url: putUrl, multipart: false, expiresSeconds: UPLOAD_URL_SECONDS });
  }

  const partSize = Number(env.PART_SIZE_BYTES) || 32 * 1024 * 1024;
  const partCount = Math.ceil(size / partSize);
  if (partCount > 10000) {
    throw new HttpError(413, "The file needs more than 10,000 parts; raise PART_SIZE_BYTES.");
  }

  const uploadId = await signer.createMultipart(key, contentType);
  const parts = [];
  for (let partNumber = 1; partNumber <= partCount; partNumber++) {
    parts.push({ partNumber, url: await signer.presignPart(key, uploadId, partNumber, UPLOAD_URL_SECONDS) });
  }
  return json({ key, uploadId, partSize, parts, multipart: true, expiresSeconds: UPLOAD_URL_SECONDS });
}

interface CompleteUploadRequest {
  key?: string;
  uploadId?: string;
  parts?: { partNumber: number; etag: string }[];
}

async function completeUpload(request: Request, env: Env): Promise<Response> {
  const body = (await readJson(request)) as CompleteUploadRequest;
  const key = requireKey(body.key);
  const signer = makeSigner(env);
  if (body.uploadId) {
    if (!Array.isArray(body.parts) || body.parts.length === 0) {
      throw new HttpError(400, "parts are required to complete a multipart upload.");
    }
    await signer.completeMultipart(key, body.uploadId, body.parts);
  }

  const size = await signer.head(key);
  if (size === null) {
    throw new HttpError(404, "The upload was not found; upload the file again.");
  }
  return json({ key, sizeBytes: size });
}

// ---- submissions -----------------------------------------------------------------------------

interface SubmissionRequest {
  kind?: string;
  name?: string;
  author?: string;
  description?: string;
  tags?: string[];
  license?: string;
  existingId?: string | null;
  remove?: boolean;
  readme?: string;
  submitterHash?: string;
  packageKey?: string;
  previewKey?: string;
  coverKey?: string;
  pluginVersion?: string;
}

async function createSubmission(request: Request, env: Env): Promise<Response> {
  await enforceRateLimit(request, env, "submission");
  const body = (await readJson(request)) as SubmissionRequest;

  const kind = (body.kind ?? "").trim();
  const name = (body.name ?? "").trim();
  const author = (body.author ?? "").trim();
  const description = (body.description ?? "").trim();
  const license = (body.license ?? "CC-BY-4.0").trim();
  const existingId = (body.existingId ?? "").trim();
  const remove = body.remove === true;
  const submitterHash = (body.submitterHash ?? "").trim().toLowerCase();
  const tags = Array.isArray(body.tags) ? body.tags.map((t) => String(t).trim()).filter(Boolean).slice(0, 10) : [];

  if (!KINDS.has(kind)) throw new HttpError(400, "kind is not one of the Workshop kinds.");
  if (!/^[a-f0-9]{64}$/.test(submitterHash)) throw new HttpError(400, "submitterHash must be a SHA-256 hex string.");
  if (!remove) {
    if (name.length === 0 || name.length > 80) throw new HttpError(400, "name is required (up to 80 characters).");
    if (author.length === 0 || author.length > 40) throw new HttpError(400, "author is required (up to 40 characters).");
    if (description.length === 0 || description.length > 400) throw new HttpError(400, "description is required (up to 400 characters).");
    if (!LICENSES.has(license)) throw new HttpError(400, "license must be CC-BY-4.0 or CC0-1.0.");
    // An update without a package changes only the item's details; intake keeps its package.
    if (!body.packageKey && existingId.length === 0) throw new HttpError(400, "packageKey is required.");
  } else if (existingId.length === 0) {
    throw new HttpError(400, "existingId is required to remove an item.");
  }

  const signer = makeSigner(env);
  const files: string[] = [];
  if (!remove) {
    if (body.packageKey) {
      const packageKey = requireKey(body.packageKey);
      const size = await signer.head(packageKey);
      if (size === null) throw new HttpError(404, "The package upload was not found; upload it again.");
      files.push(`[${fileNameOf(packageKey)}](${await signer.presignGet(packageKey, DOWNLOAD_URL_SECONDS)})`);
    }
    // The alt text tells the intake parser which image is which.
    for (const [alt, key] of [["preview", body.previewKey], ["cover", body.coverKey]] as const) {
      if (key) {
        const imageKey = requireKey(key);
        if ((await signer.head(imageKey)) !== null) {
          files.push(`![${alt}](${await signer.presignGet(imageKey, DOWNLOAD_URL_SECONDS)})`);
        }
      }
    }
  }

  // The same markdown GitHub renders from the issue form, so the intake parser needs no second
  // path. Labels must match .github/ISSUE_TEMPLATE/submit.yml exactly.
  const sections: [string, string][] = [
    ["Kind", kind],
    ["Name", name || existingId],
    ["Author name", author],
    ["Description", description || "(removal)"],
    ["Tags", tags.join(", ")],
    ["License", license],
    ["Existing item id (updates only)", existingId],
    ["Request", `- [${remove ? "x" : " "}] Remove this item from the Workshop (updates only; no file needed)`],
    ["README", (body.readme ?? "").trim()],
    ["Files", files.join("\n")],
    ["Package URL (for packages over 25 MB)", ""],
    ["Submitter key (filled in by the extension)", submitterHash],
    ["Rights", "- [x] Everything in this package is my own work or licensed for redistribution under the license above, and it contains no personal progress data I want to keep private."],
  ];
  const issueBody =
    sections.map(([label, value]) => `### ${label}\n\n${value.length > 0 ? value : "_No response_"}`).join("\n\n") +
    `\n\n<!-- submitted from Playnite${body.pluginVersion ? " " + String(body.pluginVersion).slice(0, 20) : ""} -->\n`;

  const github = new GitHubClient(env.GITHUB_REPO, env.GITHUB_TOKEN);
  const title = remove ? `[Submission] Remove ${existingId}` : `[Submission] ${name}`;
  const issue = await github.createIssue(title, issueBody, ["submission"]);
  return json({ issueNumber: issue.number, issueUrl: issue.html_url });
}

async function getSubmission(number: number, env: Env): Promise<Response> {
  const github = new GitHubClient(env.GITHUB_REPO, env.GITHUB_TOKEN);
  const issue = await github.getIssue(number);
  const labels = new Set(issue.labels.map((l) => l.name));
  if (!labels.has("submission")) {
    throw new HttpError(404, "Not a submission.");
  }

  const comment = await github.getLastBotComment(number);
  const prMatch = comment ? /https:\/\/github\.com\/[^\s)]+\/pull\/\d+/.exec(comment) : null;

  let state: string;
  if (issue.state === "closed") {
    state = issue.state_reason === "not_planned" ? "closed" : "published";
  } else if (labels.has("needs-changes")) {
    state = "needs-changes";
  } else if (labels.has("in-review")) {
    state = "in-review";
  } else {
    state = "validating";
  }

  return json({
    state,
    message: comment ?? "",
    pullRequestUrl: prMatch ? prMatch[0] : null,
    issueUrl: issue.html_url,
  });
}

// ---- helpers -----------------------------------------------------------------------------------

class HttpError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

function makeSigner(env: Env): R2Signer {
  return new R2Signer(env.R2_ACCOUNT_ID, env.R2_ACCESS_KEY_ID, env.R2_SECRET_ACCESS_KEY, env.R2_BUCKET);
}

async function readJson(request: Request): Promise<unknown> {
  try {
    return await request.json();
  } catch {
    throw new HttpError(400, "The request body must be JSON.");
  }
}

function requireKey(key: unknown): string {
  if (typeof key !== "string" || !/^\d{4}-\d{2}-\d{2}\/[0-9a-f-]{36}\/[^/]+$/.test(key)) {
    throw new HttpError(400, "key is not an upload key this service issued.");
  }
  return key;
}

function fileNameOf(key: string): string {
  return key.slice(key.lastIndexOf("/") + 1);
}

function sanitizeFileName(name: string): string {
  const base = name.replace(/[\\/]/g, "").replace(/[^A-Za-z0-9._-]/g, "-").replace(/^\.+/, "").slice(0, 120);
  return base.length > 0 ? base : "package.zip";
}

async function enforceRateLimit(request: Request, env: Env, bucket: string): Promise<void> {
  const limit = Number(env.DAILY_SUBMISSIONS_PER_IP);
  if (!Number.isFinite(limit) || limit <= 0) {
    return;
  }
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const day = new Date().toISOString().slice(0, 10);
  const key = `${bucket}:${day}:${ip}`;
  const current = Number((await env.RATE.get(key)) ?? "0");
  if (current >= limit) {
    throw new HttpError(429, `Daily submission limit reached (${limit}). Try again tomorrow.`);
  }
  await env.RATE.put(key, String(current + 1), { expirationTtl: 2 * 24 * 60 * 60 });
}

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
}

function error(status: number, message: string): Response {
  return json({ error: message }, status);
}

function cors(response: Response): Response {
  // The extension is a desktop client, not a browser, but allowing cross-origin reads costs
  // nothing and lets a web page show submission status later.
  response.headers.set("Access-Control-Allow-Origin", "*");
  response.headers.set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  response.headers.set("Access-Control-Allow-Headers", "Content-Type");
  return response;
}
