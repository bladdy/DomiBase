import assert from "node:assert/strict";
import { createClient } from "../dist/index.js";

export const BASE_URL = "http://localhost:5080";
export const PUBLIC_KEY = "db_public_test_key_1234567890";

export function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "content-type": "application/json" }
  });
}

export function rowsResponse(rows = [], count = rows.length) {
  return jsonResponse({ data: rows, count });
}

export function errorResponse(status, code, message, extra = {}) {
  return jsonResponse({ error: { code, message, ...extra } }, status);
}

/**
 * Records every fetch call and delegates the response to a handler.
 * The default handler returns an empty row list.
 */
export function createRecorder(handler) {
  const calls = [];

  async function fetchImpl(input, init = {}) {
    const call = {
      url: String(input),
      method: init.method ?? "GET",
      headers: init.headers ?? {},
      body: init.body,
      signal: init.signal
    };

    calls.push(call);

    return handler === undefined
      ? rowsResponse()
      : handler(call, calls.length);
  }

  return { calls, fetchImpl };
}

export function createTestClient({ options = {}, handler } = {}) {
  const recorder = createRecorder(handler);
  const client = createClient({
    url: BASE_URL,
    projectKey: PUBLIC_KEY,
    options: { fetch: recorder.fetchImpl, ...options }
  });

  return { client, calls: recorder.calls };
}

export function firstCall(calls) {
  const call = calls[0];
  assert.ok(call, "expected fetch to be called exactly once");
  return call;
}

export function parseUrl(call) {
  return new URL(call.url);
}

export function filtersOf(call) {
  return parseUrl(call).searchParams.getAll("f");
}

export function sentBody(call) {
  assert.equal(typeof call.body, "string", "expected a serialized JSON body");
  return JSON.parse(call.body);
}
