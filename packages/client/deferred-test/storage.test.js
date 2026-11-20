import assert from "node:assert/strict";
import { test } from "node:test";
import { createTestClient, jsonResponse, errorResponse, parseUrl, firstCall, BASE_URL, PUBLIC_KEY } from "../test/helpers.js";

const object = {
  id: "0199f000-0000-7000-8000-000000000001",
  path: "notes/hello.txt",
  contentType: "text/plain",
  size: 5,
  etag: "abc123",
  createdAt: "2026-11-17T00:00:00Z"
};

test("storage.from upload sends raw bytes with content type and parses data envelope", async () => {
  const { client, calls } = createTestClient({
    handler: () => jsonResponse({ data: object })
  });

  const result = await client.storage.from("Archivos").upload("notes/hello.txt", "hello");

  assert.equal(result.error, null);
  assert.equal(result.data.path, "notes/hello.txt");
  const call = firstCall(calls);
  assert.equal(call.method, "POST");
  assert.equal(call.url, `${BASE_URL}/storage/v1/archivos/notes/hello.txt`);
  assert.equal(call.body, "hello");
  assert.equal(call.headers["Content-Type"], "text/plain");
  assert.equal(call.headers.apikey, PUBLIC_KEY);
});

test("storage upload encodes each path segment and rejects traversal", async () => {
  const { client } = createTestClient({ handler: () => jsonResponse({ data: object }) });

  await assert.rejects(
    client.storage.from("bucket").upload("../escape.txt", "bad"),
    (error) => error.code === "INVALID_ARGUMENT"
  );

  const { client: client2, calls } = createTestClient({ handler: () => jsonResponse({ data: object }) });
  await client2.storage.from("bucket").upload("a b/café.txt", "x");
  assert.equal(firstCall(calls).url, `${BASE_URL}/storage/v1/bucket/a%20b/caf%C3%A9.txt`);
});

test("storage list sends prefix and returns objects", async () => {
  const { client, calls } = createTestClient({
    handler: () => jsonResponse({ data: [object] })
  });

  const result = await client.storage.from("bucket").list({ prefix: "notes/" });

  assert.equal(result.error, null);
  assert.equal(result.data.length, 1);
  assert.equal(parseUrl(firstCall(calls)).searchParams.get("prefix"), "notes/");
});

test("storage download returns a Blob and propagates errors", async () => {
  const { client } = createTestClient({
    handler: () => new Response("file bytes", { status: 200, headers: { "content-type": "text/plain" } })
  });

  const downloaded = await client.storage.from("bucket").download("notes/hello.txt");
  assert.equal(downloaded.error, null);
  assert.equal(await downloaded.data.text(), "file bytes");

  const { client: failing } = createTestClient({
    handler: () => errorResponse(403, "FORBIDDEN", "Not authorized to read this bucket.")
  });
  const failed = await failing.storage.from("bucket").download("notes/hello.txt");
  assert.equal(failed.data, null);
  assert.equal(failed.error.code, "FORBIDDEN");
  assert.equal(failed.error.status, 403);
});

test("storage remove issues one DELETE per path and reports the first error", async () => {
  const { client, calls } = createTestClient({
    handler: (call) => call.url.endsWith("bad.txt")
      ? errorResponse(403, "FORBIDDEN", "Not authorized to delete from this bucket.")
      : new Response(null, { status: 204 })
  });

  const result = await client.storage.from("bucket").remove(["good.txt", "bad.txt"]);
  assert.equal(result.data, null);
  assert.equal(result.error.code, "FORBIDDEN");
  assert.equal(calls.length, 2);
  assert.equal(calls[0].method, "DELETE");
});

test("storage createSignedUrl sends expiresIn and parses the data envelope", async () => {
  const { client, calls } = createTestClient({
    handler: () => jsonResponse({ data: { url: "/storage/v1/bucket/file.txt?expires=1&signature=abc" } })
  });

  const result = await client.storage.from("bucket").createSignedUrl("file.txt", { expiresIn: 600 });
  assert.equal(result.error, null);
  assert.ok(result.data.url.includes("signature=abc"));
  assert.equal(parseUrl(firstCall(calls)).searchParams.get("expiresIn"), "600");
});

test("storage validates bucket names locally", async () => {
  const { client } = createTestClient();
  for (const name of ["ab", "-bad", "with space", "UPPER".repeat(10)]) {
    assert.throws(() => client.storage.from(name), (error) => error.code === "INVALID_ARGUMENT");
  }
});
