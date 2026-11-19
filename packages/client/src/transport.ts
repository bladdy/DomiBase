import { DomiBaseError, type DomiBaseErrorShape } from "./errors.js";
import type { DomiBaseResponse } from "./types.js";

export type HttpMethod = "GET" | "POST" | "PATCH" | "DELETE";

export type AuthHeaderProvider = () => string | null;

export interface NormalizedConfig {
  url: string;
  projectKey: string;
  timeout?: number;
  headers: Record<string, string>;
  fetchImpl: typeof fetch;
  authHeaderProvider?: AuthHeaderProvider;
}

export interface WireRequest {
  method: HttpMethod;
  path: string;
  query: URLSearchParams;
  body?: unknown;
  signal?: AbortSignal;
}

/** A request whose body is raw bytes (storage uploads) instead of JSON. */
export interface BinaryWireRequest extends Omit<WireRequest, "body"> {
  body: BodyInit;
  contentType?: string;
}

interface Deadline {
  signal: AbortSignal | undefined;
  timedOut: () => boolean;
  externallyAborted: () => boolean;
  cleanup: () => void;
}

/**
 * HTTP transport for the Runtime API. It only knows about the canonical
 * `{ data, count }` / `{ error }` envelopes and never leaks response bodies
 * or internal errors to the caller.
 */
export class HttpTransport {
  constructor(private readonly config: NormalizedConfig) {}

  async send<T>(request: WireRequest): Promise<DomiBaseResponse<T>> {
    const init = this.buildInit(request);
    if (request.body !== undefined) {
      (init.headers as Record<string, string>)["Content-Type"] = "application/json";
      init.body = JSON.stringify(request.body);
    }

    return this.executeJson<T>(request, init);
  }

  /** Sends raw bytes and parses the canonical JSON envelope back. */
  async sendBytes<T>(request: BinaryWireRequest): Promise<DomiBaseResponse<T>> {
    const init = this.buildInit(request);
    if (request.contentType !== undefined) {
      (init.headers as Record<string, string>)["Content-Type"] = request.contentType;
    }
    init.body = request.body;

    return this.executeJson<T>(request, init);
  }

  /**
   * Performs a request and returns the raw response, used for binary downloads.
   * Error responses are parsed into a DomiBaseError; successful responses are
   * handed back untouched so the caller can read bytes.
   */
  async sendRaw(request: WireRequest): Promise<{ response: Response | null; error: DomiBaseError | null }> {
    const init = this.buildInit(request);
    const deadline = createDeadline(this.config.timeout, request.signal);
    if (deadline.signal !== undefined) {
      init.signal = deadline.signal;
    }

    try {
      if (deadline.externallyAborted()) {
        return { response: null, error: new DomiBaseError({ code: "ABORTED", message: "The request was aborted." }) };
      }

      let response: Response;
      try {
        response = await this.config.fetchImpl(this.buildUrl(request), init);
      } catch {
        return {
          response: null,
          error: new DomiBaseError({
            code: this.transportErrorCode(deadline),
            message: this.transportErrorMessage(deadline)
          })
        };
      }

      if (!response.ok) {
        const text = await safeReadText(response);
        return { response: null, error: parseError(tryParse(text), response.status) };
      }

      return { response, error: null };
    } finally {
      deadline.cleanup();
    }
  }

  private buildUrl(request: WireRequest): string {
    const queryString = request.query.toString();
    return `${this.config.url}${request.path}${queryString ? `?${queryString}` : ""}`;
  }

  private buildInit(request: WireRequest): RequestInit {
    const headers: Record<string, string> = {
      Accept: "application/json",
      ...this.config.headers,
      // The project key is authoritative: custom headers cannot replace it.
      apikey: this.config.projectKey
    };

    const authHeader = this.config.authHeaderProvider?.();
    if (authHeader !== undefined && authHeader !== null) {
      headers.Authorization = authHeader;
    }

    return { method: request.method, headers };
  }

  private async executeJson<T>(request: WireRequest, init: RequestInit): Promise<DomiBaseResponse<T>> {
    const deadline = createDeadline(this.config.timeout, request.signal);
    if (deadline.signal !== undefined) {
      init.signal = deadline.signal;
    }

    try {
      if (deadline.externallyAborted()) {
        return failure("ABORTED", "The request was aborted.");
      }

      let response: Response;
      try {
        response = await this.config.fetchImpl(this.buildUrl(request), init);
      } catch {
        return failure(this.transportErrorCode(deadline), this.transportErrorMessage(deadline));
      }

      return await parseResponse<T>(response);
    } finally {
      deadline.cleanup();
    }
  }

  private transportErrorCode(deadline: Deadline): string {
    if (deadline.timedOut()) {
      return "TIMEOUT";
    }

    return deadline.externallyAborted() ? "ABORTED" : "NETWORK_ERROR";
  }

  private transportErrorMessage(deadline: Deadline): string {
    if (deadline.timedOut()) {
      return "The request timed out.";
    }

    return deadline.externallyAborted()
      ? "The request was aborted."
      : "The server could not be reached.";
  }
}

async function safeReadText(response: Response): Promise<string> {
  try {
    return await response.text();
  } catch {
    return "";
  }
}

function tryParse(text: string): unknown {
  if (text.trim().length === 0) {
    return undefined;
  }

  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

async function parseResponse<T>(response: Response): Promise<DomiBaseResponse<T>> {
  const text = await safeReadText(response);
  const payload = text.trim().length > 0 ? tryParse(text) : undefined;

  if (!response.ok) {
    return { data: null, error: parseError(payload, response.status), count: null };
  }

  if (!isRecord(payload) || !("data" in payload)) {
    return failure("INVALID_RESPONSE", "The server response is missing the data envelope.", response.status);
  }

  const count = typeof payload.count === "number" ? payload.count : null;
  return { data: payload.data as T, error: null, count };
}

function parseError(payload: unknown, status: number): DomiBaseError {
  if (isRecord(payload) && isRecord(payload.error)) {
    const wire = payload.error;
    const code = typeof wire.code === "string" ? wire.code : "HTTP_ERROR";
    const message =
      typeof wire.message === "string" && wire.message.length > 0
        ? wire.message
        : `The request failed with status ${status}.`;

    const parsed: DomiBaseErrorShape = { code, message, status };
    const details = optionalString(wire.details);
    const hint = optionalString(wire.hint);

    if (details !== undefined) {
      parsed.details = details;
    }

    if (hint !== undefined) {
      parsed.hint = hint;
    }

    return new DomiBaseError(parsed);
  }

  return new DomiBaseError({
    code: "HTTP_ERROR",
    message: `The request failed with status ${status}.`,
    status
  });
}

function createDeadline(timeout: number | undefined, external: AbortSignal | undefined): Deadline {
  if (timeout === undefined && external === undefined) {
    return {
      signal: undefined,
      timedOut: () => false,
      externallyAborted: () => false,
      cleanup: () => undefined
    };
  }

  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const onAbort = (): void => controller.abort(external?.reason);

  if (external !== undefined) {
    if (external.aborted) {
      onAbort();
    } else {
      external.addEventListener("abort", onAbort, { once: true });
    }
  }

  if (timeout !== undefined) {
    timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, timeout);
  }

  return {
    signal: controller.signal,
    timedOut: () => timedOut,
    externallyAborted: () => external?.aborted === true,
    cleanup: () => {
      if (timer !== undefined) {
        clearTimeout(timer);
      }

      external?.removeEventListener("abort", onAbort);
    }
  };
}

function failure(code: string, message: string, status?: number): DomiBaseResponse<never> {
  if (status === undefined) {
    return { data: null, error: new DomiBaseError({ code, message }), count: null };
  }

  return { data: null, error: new DomiBaseError({ code, message, status }), count: null };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function optionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}
