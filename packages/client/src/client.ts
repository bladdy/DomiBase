import { DomiBaseError } from "./errors.js";
import { QueryBuilderImpl, type QueryBuilder } from "./query-builder.js";
import { HttpTransport, type NormalizedConfig } from "./transport.js";
import type { DomiBaseConfig, Row } from "./types.js";

const PUBLIC_KEY_PREFIX = "db_public_";
const MAX_TIMEOUT_MS = 10 * 60 * 1000;
const MAX_TABLE_LENGTH = 63;

export interface DomiBaseClient {
  from<TRow extends Row = Row>(table: string): QueryBuilder<TRow>;
}

/**
 * Creates a DomiBase client. The public project key only identifies the project;
 * PostgreSQL GRANT/RLS decisions are made server-side.
 */
export function createClient(config: DomiBaseConfig): DomiBaseClient {
  const normalized = normalizeConfig(config);
  const transport = new HttpTransport(normalized);

  return {
    from<TRow extends Row = Row>(table: string): QueryBuilder<TRow> {
      return QueryBuilderImpl.create<TRow>(transport, normalizeTable(table));
    }
  };
}

function normalizeConfig(config: DomiBaseConfig): NormalizedConfig {
  if (!isRecord(config)) {
    throw invalidConfig("createClient requires a configuration object.");
  }

  const url = normalizeUrl(config.url);
  const projectKey = normalizeProjectKey(config.projectKey);
  const rawOptions: unknown = config.options ?? {};

  if (!isRecord(rawOptions)) {
    throw invalidConfig("options must be an object.");
  }

  const timeout = normalizeTimeout(rawOptions["timeout"]);
  const headers = normalizeHeaders(rawOptions["headers"]);
  const fetchImpl = normalizeFetch(rawOptions["fetch"]);

  return {
    url,
    projectKey,
    headers,
    fetchImpl,
    ...(timeout !== undefined ? { timeout } : {})
  };
}

function normalizeTimeout(value: unknown): number | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== "number" || !Number.isInteger(value) || value <= 0 || value > MAX_TIMEOUT_MS) {
    throw invalidConfig(`timeout must be an integer between 1 and ${MAX_TIMEOUT_MS} milliseconds.`);
  }

  return value;
}

function normalizeFetch(value: unknown): typeof fetch {
  if (value === undefined) {
    const fallback: unknown = (globalThis as { fetch?: unknown }).fetch;

    if (typeof fallback !== "function") {
      throw new DomiBaseError({
        code: "FETCH_UNAVAILABLE",
        message: "No fetch implementation is available. Inject one through options.fetch."
      });
    }

    return fallback as typeof fetch;
  }

  if (typeof value !== "function") {
    throw invalidConfig("options.fetch must be a function.");
  }

  return value as typeof fetch;
}

function normalizeUrl(value: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw invalidConfig("url is required.");
  }

  let parsed: URL;

  try {
    parsed = new URL(value.trim());
  } catch {
    throw invalidConfig("url must be an absolute http(s) URL.");
  }

  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw invalidConfig("url must use http or https.");
  }

  if (parsed.username.length > 0 || parsed.password.length > 0) {
    throw invalidConfig("url must not contain credentials.");
  }

  parsed.hash = "";
  parsed.search = "";

  return parsed.toString().replace(/\/+$/, "");
}

function normalizeProjectKey(value: string): string {
  if (typeof value !== "string") {
    throw invalidConfig("projectKey is required.");
  }

  const key = value.trim();

  if (!key.startsWith(PUBLIC_KEY_PREFIX) || key.length === PUBLIC_KEY_PREFIX.length) {
    throw invalidConfig("projectKey must be a public key that starts with db_public_.");
  }

  return key;
}

function normalizeHeaders(value: unknown): Record<string, string> {
  if (value === undefined) {
    return {};
  }

  if (!isRecord(value)) {
    throw invalidConfig("options.headers must be an object of string values.");
  }

  const headers: Record<string, string> = {};

  for (const [name, headerValue] of Object.entries(value)) {
    if (name.trim().length === 0 || typeof headerValue !== "string") {
      throw invalidConfig("options.headers must contain non-empty names and string values.");
    }

    headers[name] = headerValue;
  }

  return headers;
}

function normalizeTable(value: string): string {
  if (typeof value !== "string") {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Table name must be a string." });
  }

  const table = value.trim();

  if (table.length === 0 || table.length > MAX_TABLE_LENGTH) {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Table name is invalid." });
  }

  if (/[\/\u0000-\u001f]/.test(table)) {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Table name is invalid." });
  }

  return table;
}

function invalidConfig(message: string): DomiBaseError {
  return new DomiBaseError({ code: "INVALID_CONFIG", message });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
