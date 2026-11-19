import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { DomiBaseError, createClient } from "../dist/index.js";
import {
  PUBLIC_KEY,
  createRecorder,
  createTestClient,
  errorResponse,
  firstCall,
  jsonResponse,
  rowsResponse
} from "./helpers.js";

function assertConfigError(action) {
  assert.throws(action, (error) => {
    assert.ok(error instanceof DomiBaseError, "expected a DomiBaseError");
    assert.equal(error.code, "INVALID_CONFIG");
    return true;
  });
}

describe("transport envelopes", () => {
  it("maps the server error envelope including details, hint and status", async () => {
    const { client } = createTestClient({
      handler: () =>
        errorResponse(403, "RLS_DENIED", "The row is not visible.", {
          details: "Policy rejected the select.",
          hint: "Check the project policies."
        })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.data, null);
    assert.equal(result.count, null);
    assert.ok(result.error instanceof DomiBaseError);
    assert.equal(result.error.code, "RLS_DENIED");
    assert.equal(result.error.message, "The row is not visible.");
    assert.equal(result.error.details, "Policy rejected the select.");
    assert.equal(result.error.hint, "Check the project policies.");
    assert.equal(result.error.status, 403);
    assert.deepEqual(result.error.toJSON(), {
      code: "RLS_DENIED",
      message: "The row is not visible.",
      details: "Policy rejected the select.",
      hint: "Check the project policies.",
      status: 403
    });
  });

  it("uses HTTP_ERROR when an error response is not a JSON envelope", async () => {
    const { client } = createTestClient({
      handler: () => new Response("Bad gateway", { status: 502 })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.error?.code, "HTTP_ERROR");
    assert.equal(result.error?.status, 502);
  });

  it("uses INVALID_RESPONSE when a successful response is not JSON", async () => {
    const { client } = createTestClient({
      handler: () => new Response("<html>ok</html>", { status: 200 })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.error?.code, "INVALID_RESPONSE");
    assert.equal(result.error?.status, 200);
  });

  it("uses INVALID_RESPONSE when the data envelope is missing", async () => {
    const { client } = createTestClient({
      handler: () => jsonResponse({ rows: [] })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.error?.code, "INVALID_RESPONSE");
  });

  it("treats a missing count as null", async () => {
    const { client } = createTestClient({
      handler: () => jsonResponse({ data: [{ id: 1 }] })
    });

    const result = await client.from("contactos").select("id");

    assert.deepEqual(result.data, [{ id: 1 }]);
    assert.equal(result.count, null);
  });
});

describe("transport failures", () => {
  it("maps a rejected fetch to NETWORK_ERROR", async () => {
    const { client } = createTestClient({
      handler: () => {
        throw new TypeError("fetch failed");
      }
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "NETWORK_ERROR");
    assert.equal(result.error?.status, undefined);
  });

  it("maps a timeout to TIMEOUT", async () => {
    const { client } = createTestClient({
      options: { timeout: 20 },
      handler: (call) =>
        new Promise((resolve, reject) => {
          call.signal.addEventListener("abort", () => reject(new Error("aborted")));
        })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.error?.code, "TIMEOUT");
  });

  it("maps an already aborted signal to ABORTED without calling fetch", async () => {
    const { client, calls } = createTestClient();
    const controller = new AbortController();
    controller.abort();

    const result = await client.from("contactos").select("id").abortSignal(controller.signal);

    assert.equal(result.error?.code, "ABORTED");
    assert.equal(calls.length, 0);
  });

  it("maps an abort during flight to ABORTED", async () => {
    const { client } = createTestClient({
      handler: (call) =>
        new Promise((resolve, reject) => {
          call.signal.addEventListener("abort", () => reject(new Error("aborted")));
        })
    });

    const controller = new AbortController();
    const pending = client
      .from("contactos")
      .select("id")
      .abortSignal(controller.signal)
      .then((value) => value);

    controller.abort();
    const result = await pending;

    assert.equal(result.error?.code, "ABORTED");
  });
});

describe("client configuration", () => {
  const fetchImpl = async () => rowsResponse();

  it("normalizes the base URL", async () => {
    const calls = [];
    const client = createClient({
      url: "http://localhost:5080/base/?token=1#frag",
      projectKey: PUBLIC_KEY,
      options: {
        fetch: async (input, init = {}) => {
          calls.push({ url: String(input), init });
          return rowsResponse();
        }
      }
    });

    await client.from("contactos").select();

    assert.equal(firstCall(calls).url, "http://localhost:5080/base/rest/v1/contactos");
  });

  it("rejects invalid URLs", () => {
    assertConfigError(() => createClient({ url: "", projectKey: PUBLIC_KEY, options: { fetch: fetchImpl } }));
    assertConfigError(() => createClient({ url: "localhost:5080", projectKey: PUBLIC_KEY, options: { fetch: fetchImpl } }));
    assertConfigError(() => createClient({ url: "ftp://localhost:5080", projectKey: PUBLIC_KEY, options: { fetch: fetchImpl } }));
    assertConfigError(() =>
      createClient({ url: "http://user:secret@localhost:5080", projectKey: PUBLIC_KEY, options: { fetch: fetchImpl } })
    );
  });

  it("rejects non public project keys", () => {
    assertConfigError(() => createClient({ url: "http://localhost:5080", projectKey: "secret", options: { fetch: fetchImpl } }));
    assertConfigError(() => createClient({ url: "http://localhost:5080", projectKey: "db_public_", options: { fetch: fetchImpl } }));
    assertConfigError(() => createClient({ url: "http://localhost:5080", projectKey: 123, options: { fetch: fetchImpl } }));
  });

  it("trims a public project key", async () => {
    const recorder = createRecorder();
    const client = createClient({
      url: "http://localhost:5080",
      projectKey: `  ${PUBLIC_KEY}  `,
      options: { fetch: recorder.fetchImpl }
    });

    await client.from("contactos").select();

    assert.equal(firstCall(recorder.calls).headers["apikey"], PUBLIC_KEY);
  });

  it("rejects invalid timeouts", () => {
    for (const timeout of [0, -1, 1.5, 600_001, "100"]) {
      assertConfigError(() =>
        createClient({
          url: "http://localhost:5080",
          projectKey: PUBLIC_KEY,
          options: { fetch: fetchImpl, timeout }
        })
      );
    }
  });

  it("rejects invalid headers", () => {
    assertConfigError(() =>
      createClient({
        url: "http://localhost:5080",
        projectKey: PUBLIC_KEY,
        options: { fetch: fetchImpl, headers: [] }
      })
    );
    assertConfigError(() =>
      createClient({
        url: "http://localhost:5080",
        projectKey: PUBLIC_KEY,
        options: { fetch: fetchImpl, headers: { "x-test": 1 } }
      })
    );
    assertConfigError(() =>
      createClient({
        url: "http://localhost:5080",
        projectKey: PUBLIC_KEY,
        options: { fetch: fetchImpl, headers: { "": "value" } }
      })
    );
  });

  it("rejects a non function fetch", () => {
    assertConfigError(() =>
      createClient({
        url: "http://localhost:5080",
        projectKey: PUBLIC_KEY,
        options: { fetch: "not-a-function" }
      })
    );
  });

  it("rejects a non object configuration or options object", () => {
    assertConfigError(() => createClient(null));
    assertConfigError(() =>
      createClient({
        url: "http://localhost:5080",
        projectKey: PUBLIC_KEY,
        options: []
      })
    );
  });

  it("reports FETCH_UNAVAILABLE when no fetch exists", { skip: !isFetchConfigurable() }, () => {
    const original = globalThis.fetch;

    try {
      delete globalThis.fetch;

      assert.throws(
        () => createClient({ url: "http://localhost:5080", projectKey: PUBLIC_KEY }),
        (error) => {
          assert.ok(error instanceof DomiBaseError);
          assert.equal(error.code, "FETCH_UNAVAILABLE");
          return true;
        }
      );
    } finally {
      globalThis.fetch = original;
    }
  });
});

function isFetchConfigurable() {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, "fetch");
  return descriptor !== undefined && descriptor.configurable;
}
