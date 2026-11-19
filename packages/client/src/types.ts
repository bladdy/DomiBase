import type { DomiBaseError } from "./errors.js";

/** A value the SDK can serialize into a Runtime filter. */
export type FilterValue = string | number | boolean | Date;

/** A row is intentionally open: the project schema is defined in PostgreSQL. */
export type Row = Record<string, unknown>;

export interface OrderOptions {
  /** Defaults to true. */
  ascending?: boolean;
}

export interface DomiBaseClientOptions {
  /** Request timeout in milliseconds. */
  timeout?: number;
  /** Extra HTTP headers. `apikey` is always derived from `projectKey`. */
  headers?: Record<string, string>;
  /** Injectable fetch implementation for tests, proxies or non-standard runtimes. */
  fetch?: typeof fetch;
}

export interface DomiBaseConfig {
  /** Runtime API base URL, for example `http://localhost:5080`. */
  url: string;
  /** Public project key: `db_public_...`. */
  projectKey: string;
  options?: DomiBaseClientOptions;
}

export interface DomiBaseResponse<T> {
  data: T | null;
  error: DomiBaseError | null;
  count?: number | null;
}
