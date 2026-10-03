import { AwsClient } from "aws4fetch";

/**
 * Presigned S3-compatible access to the temporary upload bucket. The Worker signs; bytes flow
 * straight between the extension and R2, and later between R2 and the intake workflow.
 */
export class R2Signer {
  private readonly client: AwsClient;
  private readonly endpoint: string;

  constructor(accountId: string, accessKeyId: string, secretAccessKey: string, private readonly bucket: string) {
    this.client = new AwsClient({ accessKeyId, secretAccessKey, service: "s3", region: "auto" });
    this.endpoint = `https://${accountId}.r2.cloudflarestorage.com`;
  }

  private objectUrl(key: string, query: Record<string, string> = {}): URL {
    const url = new URL(`${this.endpoint}/${this.bucket}/${encodeKey(key)}`);
    for (const [name, value] of Object.entries(query)) {
      url.searchParams.set(name, value);
    }
    return url;
  }

  /** A URL the extension can PUT the whole object to. */
  async presignPut(key: string, expiresSeconds: number): Promise<string> {
    const url = this.objectUrl(key, { "X-Amz-Expires": String(expiresSeconds) });
    const signed = await this.client.sign(new Request(url, { method: "PUT" }), { aws: { signQuery: true } });
    return signed.url;
  }

  /** A URL the intake workflow can GET the object from; valid up to 7 days. */
  async presignGet(key: string, expiresSeconds: number): Promise<string> {
    const url = this.objectUrl(key, { "X-Amz-Expires": String(expiresSeconds) });
    const signed = await this.client.sign(new Request(url, { method: "GET" }), { aws: { signQuery: true } });
    return signed.url;
  }

  async createMultipart(key: string, contentType: string): Promise<string> {
    const response = await this.client.fetch(this.objectUrl(key, { uploads: "" }).toString(), {
      method: "POST",
      headers: { "Content-Type": contentType },
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`R2 CreateMultipartUpload failed: ${response.status} ${text}`);
    }
    const match = /<UploadId>([^<]+)<\/UploadId>/.exec(text);
    if (!match) {
      throw new Error("R2 CreateMultipartUpload returned no UploadId.");
    }
    return match[1];
  }

  async presignPart(key: string, uploadId: string, partNumber: number, expiresSeconds: number): Promise<string> {
    const url = this.objectUrl(key, {
      partNumber: String(partNumber),
      uploadId,
      "X-Amz-Expires": String(expiresSeconds),
    });
    const signed = await this.client.sign(new Request(url, { method: "PUT" }), { aws: { signQuery: true } });
    return signed.url;
  }

  async completeMultipart(key: string, uploadId: string, parts: { partNumber: number; etag: string }[]): Promise<void> {
    const body =
      `<CompleteMultipartUpload>` +
      parts
        .slice()
        .sort((a, b) => a.partNumber - b.partNumber)
        .map((p) => `<Part><PartNumber>${p.partNumber}</PartNumber><ETag>${escapeXml(p.etag)}</ETag></Part>`)
        .join("") +
      `</CompleteMultipartUpload>`;
    const response = await this.client.fetch(this.objectUrl(key, { uploadId }).toString(), {
      method: "POST",
      headers: { "Content-Type": "application/xml" },
      body,
    });
    if (!response.ok) {
      throw new Error(`R2 CompleteMultipartUpload failed: ${response.status} ${await response.text()}`);
    }
  }

  async abortMultipart(key: string, uploadId: string): Promise<void> {
    await this.client.fetch(this.objectUrl(key, { uploadId }).toString(), { method: "DELETE" });
  }

  /**
   * Bytes currently stored in the bucket, summed over every object. The bucket only ever holds
   * uploads awaiting intake (a day at most), so this stays small and the listing is one or two
   * requests.
   */
  async totalBytes(): Promise<number> {
    let total = 0;
    let continuation: string | undefined;
    do {
      const query: Record<string, string> = { "list-type": "2", "max-keys": "1000" };
      if (continuation) {
        query["continuation-token"] = continuation;
      }
      const url = new URL(`${this.endpoint}/${this.bucket}`);
      for (const [name, value] of Object.entries(query)) {
        url.searchParams.set(name, value);
      }
      const response = await this.client.fetch(url.toString(), { method: "GET" });
      const text = await response.text();
      if (!response.ok) {
        throw new Error(`R2 ListObjectsV2 failed: ${response.status} ${text}`);
      }
      for (const match of text.matchAll(/<Size>(\d+)<\/Size>/g)) {
        total += Number(match[1]);
      }
      const next = /<NextContinuationToken>([^<]+)<\/NextContinuationToken>/.exec(text);
      continuation = next ? decodeXml(next[1]) : undefined;
    } while (continuation);
    return total;
  }

  /** The object's size, or null when it does not exist. */
  async head(key: string): Promise<number | null> {
    const response = await this.client.fetch(this.objectUrl(key).toString(), { method: "HEAD" });
    if (response.status === 404) {
      return null;
    }
    if (!response.ok) {
      throw new Error(`R2 HEAD failed: ${response.status}`);
    }
    return Number(response.headers.get("content-length") ?? "0");
  }
}

function encodeKey(key: string): string {
  return key.split("/").map(encodeURIComponent).join("/");
}

function decodeXml(value: string): string {
  return value.replace(/&quot;/g, '"').replace(/&gt;/g, ">").replace(/&lt;/g, "<").replace(/&amp;/g, "&");
}

function escapeXml(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
