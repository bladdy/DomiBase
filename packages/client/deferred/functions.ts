import { DomiBaseError } from "./errors.js";
import type { HttpTransport, WireRequest } from "./transport.js";
import type { DomiBaseResponse } from "./types.js";

const MAX_NAME_LENGTH = 63;
const FUNCTION_NAME_PATTERN = /^[a-z0-9][a-z0-9_-]*$/;

export interface FunctionInvokeOptions {
  signal?: AbortSignal;
}

/** Client for synchronous WebAssembly functions exposed by the Runtime. */
export interface FunctionsClient {
  invoke<TResult = unknown>(
    name: string,
    payload?: unknown,
    options?: FunctionInvokeOptions
  ): Promise<DomiBaseResponse<TResult>>;
}

export class FunctionsClientImpl implements FunctionsClient {
  constructor(private readonly transport: HttpTransport) {}

  async invoke<TResult = unknown>(
    name: string,
    payload: unknown = null,
    options: FunctionInvokeOptions = {}
  ): Promise<DomiBaseResponse<TResult>> {
    const normalized = normalizeName(name);

    const request: WireRequest = {
      method: "POST",
      path: `/functions/v1/${encodeURIComponent(normalized)}`,
      query: new URLSearchParams(),
      body: payload ?? null,
      ...(options.signal !== undefined ? { signal: options.signal } : {})
    };

    return this.transport.send<TResult>(request);
  }
}

function normalizeName(value: string): string {
  if (typeof value !== "string") {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Function name must be a string." });
  }

  const name = value.trim().toLowerCase();
  if (
    name.length === 0 ||
    name.length > MAX_NAME_LENGTH ||
    !FUNCTION_NAME_PATTERN.test(name)
  ) {
    throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Function name is invalid." });
  }

  return name;
}
