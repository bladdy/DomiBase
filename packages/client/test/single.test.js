import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { DomiBaseError } from "../dist/index.js";
import { createTestClient, errorResponse, rowsResponse } from "./helpers.js";

describe("single row helpers", () => {
  it("single returns the object when exactly one row matches", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([{ id: 1, nombre: "Ana" }], 1)
    });

    const result = await client.from("contactos").select("id,nombre").eq("id", 1).single();

    assert.deepEqual(result, {
      data: { id: 1, nombre: "Ana" },
      error: null,
      count: 1
    });
  });

  it("single returns SINGLE_ROW_ERROR when no row matches", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([], 0)
    });

    const result = await client.from("contactos").select("id").eq("id", 1).single();

    assert.equal(result.data, null);
    assert.ok(result.error instanceof DomiBaseError);
    assert.equal(result.error.code, "SINGLE_ROW_ERROR");
    assert.equal(result.count, 0);
  });

  it("single returns SINGLE_ROW_ERROR when several rows match", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([{ id: 1 }, { id: 2 }], 2)
    });

    const result = await client.from("contactos").select("id").single();

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "SINGLE_ROW_ERROR");
    assert.equal(result.count, 2);
  });

  it("maybeSingle returns null without an error when no row matches", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([], 0)
    });

    const result = await client.from("contactos").select("id").eq("id", 1).maybeSingle();

    assert.deepEqual(result, { data: null, error: null, count: 0 });
  });

  it("maybeSingle returns the object when exactly one row matches", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([{ id: 1 }], 1)
    });

    const result = await client.from("contactos").select("id").maybeSingle();

    assert.deepEqual(result, { data: { id: 1 }, error: null, count: 1 });
  });

  it("maybeSingle returns SINGLE_ROW_ERROR when several rows match", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([{ id: 1 }, { id: 2 }], 2)
    });

    const result = await client.from("contactos").select("id").maybeSingle();

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "SINGLE_ROW_ERROR");
  });

  it("propagates server errors instead of converting them to a row error", async () => {
    const { client } = createTestClient({
      handler: () =>
        errorResponse(403, "PROJECT_NOT_READY", "The project is not ready to serve data requests.")
    });

    const result = await client.from("contactos").select("id").single();

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "PROJECT_NOT_READY");
    assert.equal(result.error?.status, 403);
  });

  it("keeps local validation ahead of the single row semantics", async () => {
    const { client, calls } = createTestClient();

    const result = await client.from("contactos").update({ a: 1 }).single();

    assert.equal(result.error?.code, "FILTER_REQUIRED");
    assert.equal(calls.length, 0);
  });
});
