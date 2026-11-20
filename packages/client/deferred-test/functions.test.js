import assert from "node:assert/strict";
import { test } from "node:test";
import {
  createTestClient,
  jsonResponse,
  errorResponse,
  firstCall,
  parseUrl,
  sentBody,
  BASE_URL,
  PUBLIC_KEY
} from "../test/helpers.js";

test("functions.invoke posts JSON payload and parses the data envelope", async () => {
  const resultData = { input: { nombre: "Ana" }, context: { projectId: "p1", userId: null } };
  const { client, calls } = createTestClient({
    handler: () => jsonResponse({ data: resultData })
  });

  const result = await client.functions.invoke("saludo", { nombre: "Ana" });

  assert.equal(result.error, null);
  assert.deepEqual(result.data, resultData);
  const call = firstCall(calls);
  assert.equal(call.method, "POST");
  assert.equal(call.url, `${BASE_URL}/functions/v1/saludo`);
  assert.equal(call.headers.apikey, PUBLIC_KEY);
  assert.deepEqual(sentBody(call), { nombre: "Ana" });
});

test("functions.invoke normalizes the name and sends null when payload is omitted", async () => {
  const { client, calls } = createTestClient({ handler: () => jsonResponse({ data: 1 }) });

  await client.functions.invoke("  Eco_1  ");

  assert.equal(firstCall(calls).url, `${BASE_URL}/functions/v1/eco_1`);
  assert.equal(sentBody(firstCall(calls)), null);
});

test("functions.invoke propagates canonical errors with status", async () => {
  const { client } = createTestClient({
    handler: () => errorResponse(422, "FUNCTION_OUT_OF_FUEL", "CPU budget exceeded.")
  });

  const result = await client.functions.invoke("lento", {});

  assert.equal(result.data, null);
  assert.equal(result.error.code, "FUNCTION_OUT_OF_FUEL");
  assert.equal(result.error.status, 422);
});

test("functions.invoke validates names locally and forwards AbortSignal", async () => {
  const { client } = createTestClient({ handler: () => jsonResponse({ data: null }) });

  for (const name of ["", "-malo", "con espacio", "con/barra", "x".repeat(64)]) {
    await assert.rejects(
      client.functions.invoke(name),
      (error) => error.code === "INVALID_ARGUMENT"
    );
  }

  const { client: client2, calls } = createTestClient({ handler: () => jsonResponse({ data: null }) });
  await client2.functions.invoke("eco", {});
  assert.equal(parseUrl(firstCall(calls)).pathname, "/functions/v1/eco");

  const { client: client3, calls: abortedCalls } = createTestClient({
    handler: () => jsonResponse({ data: null })
  });
  const controller = new AbortController();
  controller.abort();
  const aborted = await client3.functions.invoke("eco", {}, { signal: controller.signal });

  assert.equal(aborted.error.code, "ABORTED");
  assert.equal(abortedCalls.length, 0);
});
