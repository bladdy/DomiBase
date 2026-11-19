import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { DomiBaseError } from "../dist/index.js";
import {
  PUBLIC_KEY,
  createTestClient,
  filtersOf,
  firstCall,
  jsonResponse,
  parseUrl,
  rowsResponse,
  sentBody
} from "./helpers.js";

function rejectsWithCode(action, code) {
  assert.throws(action, (error) => {
    assert.ok(error instanceof DomiBaseError, "expected a DomiBaseError");
    assert.equal(error.code, code);
    return true;
  });
}

describe("query builder wire format", () => {
  it("builds a GET select with columns, filter, order and limit", async () => {
    const { client, calls } = createTestClient({
      handler: () => rowsResponse([{ id: 1, nombre: "Ana" }], 1)
    });

    const result = await client
      .from("contactos")
      .select("id,nombre,email")
      .eq("estado", "nuevo")
      .order("created_at", { ascending: false })
      .limit(20);

    assert.equal(calls.length, 1);
    const call = firstCall(calls);
    assert.equal(call.method, "GET");
    assert.equal(call.url, "http://localhost:5080/rest/v1/contactos?select=id%2Cnombre%2Cemail&f=estado.eq.nuevo&order=created_at.desc&limit=20");
    assert.equal(call.headers["apikey"], PUBLIC_KEY);
    assert.equal(call.headers["Accept"], "application/json");
    assert.equal(call.body, undefined);

    assert.deepEqual(result, {
      data: [{ id: 1, nombre: "Ana" }],
      error: null,
      count: 1
    });
  });

  it("omits the query string when no option is set", async () => {
    const { client, calls } = createTestClient();

    await client.from("contactos").select();

    const call = firstCall(calls);
    assert.equal(call.url, "http://localhost:5080/rest/v1/contactos");
    assert.equal(call.method, "GET");
  });

  it("serializes every supported filter operator", async () => {
    const { client, calls } = createTestClient();

    await client
      .from("contactos")
      .select()
      .eq("a", 1)
      .neq("b", true)
      .gt("c", 1.5)
      .gte("d", "texto")
      .lt("e", new Date("2026-01-02T03:04:05.000Z"))
      .lte("f", false)
      .like("g", "%ana%")
      .ilike("h", "%perez%")
      .in("i", [1, "dos", true])
      .is("j", null);

    assert.deepEqual(filtersOf(firstCall(calls)), [
      "a.eq.1",
      "b.neq.true",
      "c.gt.1.5",
      "d.gte.texto",
      "e.lt.2026-01-02T03:04:05.000Z",
      "f.lte.false",
      "g.like.%ana%",
      "h.ilike.%perez%",
      "i.in.1,dos,true",
      "j.is.null"
    ]);
  });

  it("serializes is with true and false", async () => {
    const { client, calls } = createTestClient();

    await client.from("contactos").select("id").is("activo", true);
    await client.from("contactos").select("id").is("inactivo", false);

    assert.deepEqual(filtersOf(firstCall(calls)), ["activo.is.true"]);
    assert.deepEqual(filtersOf(calls[1]), ["inactivo.is.false"]);
  });

  it("supports multiple order columns with asc as the default", async () => {
    const { client, calls } = createTestClient();

    await client
      .from("contactos")
      .select("id")
      .order("nombre")
      .order("created_at", { ascending: true })
      .order("id", { ascending: false });

    assert.equal(
      parseUrl(firstCall(calls)).searchParams.get("order"),
      "nombre.asc,created_at.asc,id.desc"
    );
  });

  it("maps range to offset and limit", async () => {
    const { client, calls } = createTestClient();

    await client.from("contactos").select("id").range(10, 19);

    const search = parseUrl(firstCall(calls)).searchParams;
    assert.equal(search.get("offset"), "10");
    assert.equal(search.get("limit"), "10");
  });

  it("sends inserts as JSON with the selected returning columns", async () => {
    const row = { nombre: "Ana", email: "ana@example.com" };
    const { client, calls } = createTestClient({
      handler: () => jsonResponse({ data: [{ id: 7 }], count: 1 }, 201)
    });

    const result = await client.from("contactos").insert(row).select("id");

    const call = firstCall(calls);
    assert.equal(call.method, "POST");
    assert.equal(call.headers["Content-Type"], "application/json");
    assert.equal(parseUrl(call).searchParams.get("select"), "id");
    assert.deepEqual(sentBody(call), row);
    assert.deepEqual(result.data, [{ id: 7 }]);
    assert.equal(result.count, 1);
  });

  it("sends bulk inserts as a JSON array without mutating the input", async () => {
    const rows = [{ nombre: "Ana" }, { nombre: "Luis" }];
    const { client, calls } = createTestClient({
      handler: () => jsonResponse({ data: rows, count: 2 }, 201)
    });

    await client.from("contactos").insert(rows);

    assert.deepEqual(sentBody(firstCall(calls)), rows);
    assert.deepEqual(rows, [{ nombre: "Ana" }, { nombre: "Luis" }]);
  });

  it("sends updates as PATCH with filters and a JSON body", async () => {
    const { client, calls } = createTestClient();

    await client.from("contactos").update({ estado: "contactado" }).eq("id", 3);

    const call = firstCall(calls);
    assert.equal(call.method, "PATCH");
    assert.equal(parseUrl(call).searchParams.get("select"), null);
    assert.deepEqual(filtersOf(call), ["id.eq.3"]);
    assert.equal(call.headers["Content-Type"], "application/json");
    assert.deepEqual(sentBody(call), { estado: "contactado" });
  });

  it("sends deletes as DELETE without a body", async () => {
    const { client, calls } = createTestClient();

    await client.from("contactos").delete().eq("id", 3);

    const call = firstCall(calls);
    assert.equal(call.method, "DELETE");
    assert.equal(call.body, undefined);
    assert.equal(call.headers["Content-Type"], undefined);
    assert.deepEqual(filtersOf(call), ["id.eq.3"]);
  });
});

describe("query builder local validation", () => {
  it("rejects invalid select column lists", () => {
    const { client, calls } = createTestClient();

    rejectsWithCode(() => client.from("contactos").select(""), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").select("id,,nombre"), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").select("id,id"), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").select("id.nombre"), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").select("id\nnombre"), "INVALID_ARGUMENT");
    rejectsWithCode(
      () => client.from("contactos").select(Array.from({ length: 51 }, (_, index) => `c${index}`).join(",")),
      "INVALID_ARGUMENT"
    );

    assert.equal(calls.length, 0);
  });

  it("rejects invalid filter values", () => {
    const { client } = createTestClient();

    rejectsWithCode(() => client.from("contactos").eq("valor", Number.NaN), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").gte("valor", Number.POSITIVE_INFINITY), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").lt("valor", new Date("invalid")), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").eq(null, 1), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").eq("a.b", 1), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").eq("a,b", 1), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").in("valor", []), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").in("valor", ["a,b"]), "INVALID_ARGUMENT");
    rejectsWithCode(
      () => client.from("contactos").in("valor", Array.from({ length: 101 }, (_, index) => index)),
      "INVALID_ARGUMENT"
    );
    rejectsWithCode(() => client.from("contactos").like("valor", 10), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").is("valor", "null"), "INVALID_ARGUMENT");
  });

  it("rejects more than ten filters", () => {
    const { client } = createTestClient();
    let builder = client.from("contactos").select("id");

    for (let index = 0; index < 10; index += 1) {
      builder = builder.eq(`c${index}`, index);
    }

    rejectsWithCode(() => builder.eq("c10", 10), "INVALID_ARGUMENT");
  });

  it("rejects more than three order columns", () => {
    const { client } = createTestClient();
    const builder = client.from("contactos").select("id").order("a").order("b").order("c");

    rejectsWithCode(() => builder.order("d"), "INVALID_ARGUMENT");
  });

  it("rejects out-of-range limits and ranges", () => {
    const { client } = createTestClient();

    rejectsWithCode(() => client.from("contactos").limit(0), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").limit(1001), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").limit(1.5), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").range(10, 9), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").range(0, 1000), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").range(-1, 10), "INVALID_ARGUMENT");
  });

  it("rejects empty or invalid inserts and updates", () => {
    const { client } = createTestClient();

    rejectsWithCode(() => client.from("contactos").insert([]), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").insert([{}]), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").insert(["no-es-fila"]), "INVALID_ARGUMENT");
    rejectsWithCode(
      () => client.from("contactos").insert(Array.from({ length: 101 }, () => ({ a: 1 }))),
      "INVALID_ARGUMENT"
    );
    rejectsWithCode(() => client.from("contactos").update({}), "INVALID_ARGUMENT");
  });

  it("rejects invalid table names before building a request", () => {
    const { client, calls } = createTestClient();

    rejectsWithCode(() => client.from(""), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("   "), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos/otra"), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos\notra"), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("t".repeat(64)), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from(null), "INVALID_ARGUMENT");

    assert.equal(calls.length, 0);
  });

  it("rejects invalid abort signals", () => {
    const { client } = createTestClient();

    rejectsWithCode(() => client.from("contactos").abortSignal({}), "INVALID_ARGUMENT");
    rejectsWithCode(() => client.from("contactos").abortSignal(null), "INVALID_ARGUMENT");
  });

  it("resolves INVALID_QUERY without a network call when no operation is selected", async () => {
    const { client, calls } = createTestClient();

    const result = await client.from("contactos");

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "INVALID_QUERY");
    assert.equal(calls.length, 0);
  });

  it("resolves FILTER_REQUIRED without a network call for unfiltered writes", async () => {
    const { client, calls } = createTestClient();

    const update = await client.from("contactos").update({ estado: "nuevo" });
    const remove = await client.from("contactos").delete();

    assert.equal(update.error?.code, "FILTER_REQUIRED");
    assert.equal(remove.error?.code, "FILTER_REQUIRED");
    assert.equal(calls.length, 0);
  });
});

describe("query builder behaviour", () => {
  it("is immutable so a base builder can be reused", async () => {
    const { client, calls } = createTestClient();
    const base = client.from("contactos");

    await base.select("id").eq("id", 1);
    await base.select("nombre").limit(5);
    await base.select();

    assert.equal(calls.length, 3);
    assert.equal(parseUrl(calls[0]).searchParams.get("select"), "id");
    assert.deepEqual(filtersOf(calls[0]), ["id.eq.1"]);
    assert.equal(parseUrl(calls[1]).searchParams.get("select"), "nombre");
    assert.deepEqual(filtersOf(calls[1]), []);
    assert.equal(parseUrl(calls[2]).searchParams.get("select"), null);
  });

  it("returns an INVALID_RESPONSE error when the payload is not a row list", async () => {
    const { client } = createTestClient({
      handler: () => jsonResponse({ data: { id: 1 }, count: 1 })
    });

    const result = await client.from("contactos").select("id");

    assert.equal(result.data, null);
    assert.equal(result.error?.code, "INVALID_RESPONSE");
  });

  it("propagates the server count", async () => {
    const { client } = createTestClient({
      handler: () => rowsResponse([{ id: 1 }], 42)
    });

    const result = await client.from("contactos").select("id").range(0, 0);

    assert.equal(result.count, 42);
  });

  it("sends custom headers but never lets them replace the project key", async () => {
    const { client, calls } = createTestClient({
      options: { headers: { "x-trace": "abc", apikey: "db_public_evil" } }
    });

    await client.from("contactos").select("id");

    const call = firstCall(calls);
    assert.equal(call.headers["x-trace"], "abc");
    assert.equal(call.headers["apikey"], PUBLIC_KEY);
  });
});
