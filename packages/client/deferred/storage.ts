import { DomiBaseError } from "./errors.js";
import type { BinaryWireRequest, HttpTransport, WireRequest } from "./transport.js";
import type { DomiBaseResponse } from "./types.js";

const MAX_BUCKET_LENGTH = 40;
const MAX_PATH_LENGTH = 1024;
const BUCKET_PATTERN = /^[a-z0-9][a-z0-9-]*$/;

export interface StorageObject {
  id: string;
  path: string;
  contentType: string | null;
  size: number;
  etag: string;
  createdAt: string;
}

export interface SignedUrl {
  url: string;
}

export interface StorageUploadOptions {
  contentType?: string;
  signal?: AbortSignal;
}

export interface StorageListOptions {
  prefix?: string;
  signal?: AbortSignal;
}

export interface StorageRemoveOptions {
  signal?: AbortSignal;
}

export interface StorageSignedUrlOptions {
  /** Lifetime in seconds. Defaults to 3600. */
  expiresIn?: number;
  signal?: AbortSignal;
}

/** A bucket-scoped handle returned by `db.storage.from(name)`. */
export interface StorageBucketClient {
  upload(path: string, body: Blob | ArrayBuffer | ArrayBufferView | string, options?: StorageUploadOptions): Promise<DomiBaseResponse<StorageObject>>;
  download(path: string, options?: { signal?: AbortSignal }): Promise<DomiBaseResponse<Blob>>;
  list(options?: StorageListOptions): Promise<DomiBaseResponse<StorageObject[]>>;
  remove(paths: string[], options?: StorageRemoveOptions): Promise<DomiBaseResponse<boolean>>;
  createSignedUrl(path: string, options?: StorageSignedUrlOptions): Promise<DomiBaseResponse<SignedUrl>>;
}

export interface StorageClient {
  from(bucket: string): StorageBucketClient;
}

export class StorageClientImpl implements StorageClient {
  constructor(private readonly transport: HttpTransport) {}

  from(bucket: string): StorageBucketClient {
    const normalized = normalizeBucket(bucket);
    return new StorageBucketClientImpl(this.transport, normalized);
  }
}

class StorageBucketClientImpl implements StorageBucketClient {
  constructor(
    private readonly transport: HttpTransport,
    private readonly bucket: string
  ) {}

  async upload(
    path: string,
    body: Blob | ArrayBuffer | ArrayBufferView | string,
    options: StorageUploadOptions = {}
  ): Promise<DomiBaseResponse<StorageObject>> {
    const safePath = normalizePath(path);
    const { contentType, signal } = options;

    let payload: BodyInit;
    let inferredType: string | undefined;

    if (typeof Blob !== "undefined" && body instanceof Blob) {
      payload = body;
      inferredType = body.type || undefined;
    } else if (typeof body === "string") {
      payload = body;
      inferredType = "text/plain";
    } else if (ArrayBuffer.isView(body)) {
      payload = body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength) as ArrayBuffer;
    } else {
      payload = body as ArrayBuffer;
    }

    const resolvedContentType = contentType ?? inferredType;
    const request: BinaryWireRequest = {
      method: "POST",
      path: `/storage/v1/${encodeURIComponent(this.bucket)}/${encodePath(safePath)}`,
      query: new URLSearchParams(),
      body: payload,
      ...(resolvedContentType !== undefined ? { contentType: resolvedContentType } : {}),
      ...(signal !== undefined ? { signal } : {})
    };

    return this.transport.sendBytes<StorageObject>(request);
  }

  async download(path: string, options: { signal?: AbortSignal } = {}): Promise<DomiBaseResponse<Blob>> {
    const safePath = normalizePath(path);
    const request: WireRequest = {
      method: "GET",
      path: `/storage/v1/${encodeURIComponent(this.bucket)}/${encodePath(safePath)}`,
      query: new URLSearchParams(),
      ...(options.signal ? { signal: options.signal } : {})
    };

    const { response, error } = await this.transport.sendRaw(request);
    if (error !== null || response === null) {
      return { data: null, error, count: null };
    }

    try {
      const blob = await response.blob();
      return { data: blob, error: null, count: null };
    } catch {
      return {
        data: null,
        error: new DomiBaseError({ code: "INVALID_RESPONSE", message: "The downloaded object could not be read." }),
        count: null
      };
    }
  }

  async list(options: StorageListOptions = {}): Promise<DomiBaseResponse<StorageObject[]>> {
    const query = new URLSearchParams();
    if (options.prefix !== undefined && options.prefix.length > 0) {
      query.set("prefix", options.prefix);
    }

    const request: WireRequest = {
      method: "GET",
      path: `/storage/v1/${encodeURIComponent(this.bucket)}`,
      query,
      ...(options.signal ? { signal: options.signal } : {})
    };

    return this.transport.send<StorageObject[]>(request);
  }

  async remove(paths: string[], options: StorageRemoveOptions = {}): Promise<DomiBaseResponse<boolean>> {
    if (!Array.isArray(paths) || paths.length === 0) {
      return {
        data: null,
        error: new DomiBaseError({ code: "INVALID_ARGUMENT", message: "At least one path is required." }),
        count: null
      };
    }

    const normalized = paths.map(normalizePath);

    // The backend deletes one object per request; issue them sequentially so a
    // partial failure is reported to the caller instead of silently swallowed.
    let firstError: DomiBaseError | null = null;
    for (const path of normalized) {
      const response = await this.transport.sendRaw({
        method: "DELETE",
        path: `/storage/v1/${encodeURIComponent(this.bucket)}/${encodePath(path)}`,
        query: new URLSearchParams(),
        ...(options.signal ? { signal: options.signal } : {})
      });

      if (response.error !== null && firstError === null) {
        firstError = response.error;
      }
    }

    return firstError === null
      ? { data: true, error: null, count: null }
      : { data: null, error: firstError, count: null };
  }

  async createSignedUrl(
    path: string,
    options: StorageSignedUrlOptions = {}
  ): Promise<DomiBaseResponse<SignedUrl>> {
    const safePath = normalizePath(path);
    const query = new URLSearchParams();
    if (options.expiresIn !== undefined) {
      query.set("expiresIn", String(options.expiresIn));
    }

    const request: WireRequest = {
      method: "POST",
      path: `/storage/v1/${encodeURIComponent(this.bucket)}/signed/${encodePath(safePath)}`,
      query,
      ...(options.signal ? { signal: options.signal } : {})
    };

    return this.transport.send<SignedUrl>(request);
  }
}

function normalizeBucket(value: string): string {
  if (typeof value !== "string") {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Bucket name must be a string." });
  }

  const bucket = value.trim().toLowerCase();
  if (
    bucket.length < 3 ||
    bucket.length > MAX_BUCKET_LENGTH ||
    !BUCKET_PATTERN.test(bucket) ||
    bucket.endsWith("-")
  ) {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Bucket name is invalid." });
  }

  return bucket;
}

function normalizePath(value: string): string {
  if (typeof value !== "string") {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Object path must be a string." });
  }

  const path = value.trim().replaceAll("\\", "/").replace(/^\/+/, "");
  if (path.length === 0 || path.length > MAX_PATH_LENGTH || path.includes("..")) {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Object path is invalid." });
  }

  return path;
}

/** Encodes each segment so slashes remain path separators. */
function encodePath(path: string): string {
  return path.split("/").map(encodeURIComponent).join("/");
}
