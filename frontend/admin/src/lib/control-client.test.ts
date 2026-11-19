import { describe, expect, it, vi } from "vitest";
import { ControlApiError, ControlClient, type AuditQuery, type FetchLike } from "./control-client";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

function fetchSpy(response: Response): { fetchImpl: FetchLike; spy: ReturnType<typeof vi.fn> } {
  const spy = vi.fn().mockResolvedValue(response);
  return { fetchImpl: spy as unknown as FetchLike, spy };
}

const backup = {
  id: "22222222-2222-7222-8222-222222222222",
  projectId: "11111111-1111-1111-1111-111111111111",
  format: "custom",
  sizeBytes: 4096,
  sha256: "abc123def456",
  createdAt: "2026-11-17T12:00:00Z"
};

const project = {
  id: "11111111-1111-1111-1111-111111111111",
  slug: "paneles-solares",
  name: "Paneles Solares",
  status: "Ready",
  databaseName: "domibase_paneles_solares",
  failureReason: null,
  createdAt: "2026-11-17T10:00:00Z",
  updatedAt: "2026-11-17T10:01:00Z",
  rateLimitPermits: null,
  rateLimitWindowSeconds: null,
  effectiveRateLimitPermits: 300,
  effectiveRateLimitWindowSeconds: 60
};

describe("ControlClient", () => {
  it("sends the admin token as a header on every request", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse([project]));
    const client = new ControlClient({ token: "secret-token", fetch: fetchImpl });

    await expect(client.listProjects()).resolves.toEqual([project]);
    expect(spy).toHaveBeenCalledWith(
      "/api/v1/projects",
      expect.objectContaining({ headers: expect.any(Headers) })
    );
    const headers = spy.mock.calls[0]?.[1]?.headers as Headers;
    expect(headers.get("X-DomiBase-Admin-Token")).toBe("secret-token");
  });

  it("never sends a request when no session token is present", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse([]));
    const client = new ControlClient({ fetch: fetchImpl });

    await expect(client.listProjects()).rejects.toMatchObject({
      name: "ControlApiError",
      code: "NO_SESSION",
      status: 401
    });
    expect(spy).not.toHaveBeenCalled();
  });

  it("surfaces the canonical error envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse(
        { error: { code: "VALIDATION_FAILED", message: "Slug inválido", hint: "Usa kebab-case" } },
        400
      )
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    const error = await client.createProject({ slug: "x", name: "x" }).catch((value: unknown) => value);
    expect(error).toBeInstanceOf(ControlApiError);
    expect(error).toMatchObject({
      code: "VALIDATION_FAILED",
      message: "Slug inválido",
      status: 400,
      hint: "Usa kebab-case"
    });
  });

  it("maps a bare 401 to a local session message", async () => {
    const { fetchImpl } = fetchSpy(new Response("", { status: 401 }));
    const client = new ControlClient({ token: "expired", fetch: fetchImpl });

    await expect(client.listProjects()).rejects.toMatchObject({
      code: "UNAUTHORIZED",
      status: 401
    });
  });

  it("posts JSON when creating a project", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse(project, 201));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(
      client.createProject({ slug: "paneles-solares", name: "Paneles Solares" })
    ).resolves.toEqual(project);
    const init = spy.mock.calls[0]?.[1];
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe(JSON.stringify({ slug: "paneles-solares", name: "Paneles Solares" }));
    expect((init?.headers as Headers).get("Content-Type")).toBe("application/json");
  });

  it("patches the per-project rate limit with explicit nulls to inherit defaults", async () => {
    const updated = { ...project, rateLimitPermits: 12, rateLimitWindowSeconds: 90, effectiveRateLimitPermits: 12 };
    const spy = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(updated))
      .mockResolvedValueOnce(jsonResponse(project));
    const fetchImpl = spy as unknown as FetchLike;
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(
      client.updateRateLimit(project.id, { permits: 12, windowSeconds: 90 })
    ).resolves.toEqual(updated);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/rate-limit`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("PATCH");
    expect(spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify({ permits: 12, windowSeconds: 90 }));

    await client.updateRateLimit(project.id, { permits: null, windowSeconds: null });
    expect(spy.mock.calls[1]?.[1]?.body).toBe(JSON.stringify({ permits: null, windowSeconds: null }));
  });

  it("returns the one-time full key without storing it on the client", async () => {
    const created = {
      id: "key-1",
      projectId: project.id,
      kind: "Public",
      prefix: "db_public_ab12cd",
      key: "db_public_ab12cd34ef56",
      label: "astro",
      createdAt: project.createdAt
    };
    const { fetchImpl } = fetchSpy(jsonResponse(created, 201));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(
      client.createKey(project.id, { kind: "Public", label: "astro" })
    ).resolves.toEqual(created);
    expect(client.token).toBe("token");
    expect(JSON.stringify(client)).not.toContain(created.key);
  });

  it("accepts 204 for destructive operations", async () => {
    const { fetchImpl, spy } = fetchSpy(new Response(null, { status: 204 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.revokeKey(project.id, "key-1")).resolves.toBeUndefined();
    expect(spy.mock.calls[0]?.[1]?.method).toBe("DELETE");
  });

  it("forwards an AbortSignal", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse([]));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await client.listBuckets(project.id, controller.signal);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
  });

  it("maps network failures without leaking the token", async () => {
    const fetchImpl: FetchLike = vi.fn().mockRejectedValue(new TypeError("Failed to fetch"));
    const client = new ControlClient({ token: "super-secret", fetch: fetchImpl });

    const error = await client.listProjects().catch((value: unknown) => value);
    expect(error).toMatchObject({ code: "NETWORK_ERROR", status: 0 });
    expect((error as Error).message).not.toContain("super-secret");
  });

  it("rejects malformed JSON responses", async () => {
    const { fetchImpl } = fetchSpy(new Response("not-json", { status: 200 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.listFunctions(project.id)).rejects.toMatchObject({
      code: "INVALID_RESPONSE"
    });
  });

  it("allows replacing the token after a rejected session", async () => {
    const { fetchImpl } = fetchSpy(jsonResponse([]));
    const client = new ControlClient({ fetch: fetchImpl });
    client.setToken("first");
    client.setToken(null);
    await expect(client.listProjects()).rejects.toMatchObject({ code: "NO_SESSION" });

    client.setToken("second");
    await expect(client.listProjects()).resolves.toEqual([]);
  });

  it("reads public schema tables from the fixed Control Plane route", async () => {
    const tables = [{ name: "contactos" }, { name: "paneles" }];
    const { fetchImpl, spy } = fetchSpy(jsonResponse(tables));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.listSchemaTables(project.id)).resolves.toEqual(tables);
    expect(spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/tables`
    );
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe(
      "token"
    );
  });

  it("reads public schema views and functions without sending query parameters", async () => {
    const views = [{ name: "resumen_contactos" }];
    const functions = [{ name: "normalizar", arguments: "valor text", returnType: "text" }];
    const viewFetch = fetchSpy(jsonResponse(views));
    const functionFetch = fetchSpy(jsonResponse(functions));

    await expect(
      new ControlClient({ token: "token", fetch: viewFetch.fetchImpl }).listSchemaViews(project.id)
    ).resolves.toEqual(views);
    await expect(
      new ControlClient({ token: "token", fetch: functionFetch.fetchImpl }).listSchemaFunctions(project.id)
    ).resolves.toEqual(functions);

    expect(viewFetch.spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/views`
    );
    expect(functionFetch.spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/functions`
    );
  });

  it("maps schema inspection errors to the canonical envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse({ error: { code: "NOT_FOUND", message: "Proyecto no encontrado" } }, 404)
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.listSchemaTables(project.id)).rejects.toMatchObject({
      code: "NOT_FOUND",
      message: "Proyecto no encontrado",
      status: 404
    });
  });

  it("previews rows with only allowlisted query parameters and an anonymous role reported by the server", async () => {
    const result = {
      role: "anon",
      columns: ["id", "email"],
      rows: [{ id: "abc", email: "ana@example.test" }],
      rowCount: 1,
      truncated: false
    };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(result));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(
      client.previewTableRows(
        project.id,
        "contactos",
        {
          select: "id,email",
          filters: ["email.eq.ana@example.test", "estado.eq.nuevo"],
          order: "email.asc",
          limit: 25,
          offset: 10
        },
        controller.signal
      )
    ).resolves.toEqual(result);

    const url = new URL(spy.mock.calls[0]?.[0] as string, "http://localhost");
    expect(url.pathname).toBe(`/api/v1/projects/${project.id}/data/tables/contactos/rows`);
    expect(url.searchParams.get("select")).toBe("id,email");
    expect(url.searchParams.getAll("f")).toEqual([
      "email.eq.ana@example.test",
      "estado.eq.nuevo"
    ]);
    expect(url.searchParams.get("order")).toBe("email.asc");
    expect(url.searchParams.get("limit")).toBe("25");
    expect(url.searchParams.get("offset")).toBe("10");
    expect(url.searchParams.has("role")).toBe(false);
    expect(url.searchParams.has("database")).toBe(false);
    expect(url.searchParams.has("apikey")).toBe(false);
    expect(spy.mock.calls[0]?.[1]?.body).toBeUndefined();
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("omits empty preview options instead of sending them to the server", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse({ role: "anon", columns: [], rows: [], rowCount: 0, truncated: false }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await client.previewTableRows(project.id, "contactos", {
      select: "",
      filters: [""],
      order: "",
      limit: 50
    });

    const url = new URL(spy.mock.calls[0]?.[0] as string, "http://localhost");
    expect(url.searchParams.has("select")).toBe(false);
    expect(url.searchParams.has("f")).toBe(false);
    expect(url.searchParams.has("order")).toBe(false);
    expect(url.searchParams.get("limit")).toBe("50");
  });

  it("maps preview validation errors to the canonical envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "The preview request is not valid.",
            hint: "Filters must use column.operator.value."
          }
        },
        400
      )
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(
      client.previewTableRows(project.id, "contactos", { filters: ["email"] })
    ).rejects.toMatchObject({
      code: "VALIDATION_ERROR",
      status: 400,
      hint: "Filters must use column.operator.value."
    });
  });

  it("reads and mutates RLS policies through the fixed project-scoped routes", async () => {
    const policy = {
      name: "contactos_select",
      commands: ["SELECT"],
      roles: ["anon"],
      using: "true",
      withCheck: null
    };
    const mutation = { action: "policy.create", table: "contactos", object: policy.name };
    const listFetch = fetchSpy(jsonResponse([policy]));
    const createFetch = fetchSpy(jsonResponse(mutation));
    const input = {
      name: policy.name,
      commands: ["SELECT"] as const,
      roles: ["anon"] as const,
      using: "true" as const,
      withCheck: "true" as const
    };
    const controller = new AbortController();

    await expect(
      new ControlClient({ token: "token", fetch: listFetch.fetchImpl })
        .listPolicies(project.id, "contactos", controller.signal)
    ).resolves.toEqual([policy]);
    expect(listFetch.spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/tables/contactos/policies`
    );
    expect(listFetch.spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);

    const client = new ControlClient({ token: "token", fetch: createFetch.fetchImpl });
    await expect(client.createPolicy(project.id, "contactos", input)).resolves.toEqual(mutation);
    expect(createFetch.spy.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(createFetch.spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify(input));

    const updateFetch = fetchSpy(jsonResponse({ ...mutation, action: "policy.alter" }));
    await new ControlClient({ token: "token", fetch: updateFetch.fetchImpl })
      .updatePolicy(project.id, "contactos", policy.name, input);
    expect(updateFetch.spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/tables/contactos/policies/${policy.name}`
    );
    expect(updateFetch.spy.mock.calls[0]?.[1]?.method).toBe("PATCH");

    const deleteFetch = fetchSpy(new Response(null, { status: 204 }));
    await new ControlClient({ token: "token", fetch: deleteFetch.fetchImpl })
      .deletePolicy(project.id, "contactos", policy.name);
    expect(deleteFetch.spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/schema/tables/contactos/policies/${policy.name}`
    );
    expect(deleteFetch.spy.mock.calls[0]?.[1]?.method).toBe("DELETE");
  });

  it("posts a bounded read-only query to the fixed Control Plane route", async () => {
    const result = {
      columns: ["id", "nombre"],
      rows: [{ id: 1, nombre: "alfa" }],
      rowCount: 1,
      truncated: false,
      durationMs: 12
    };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(result));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    const input = { sql: "SELECT id, nombre FROM contactos", maxRows: 50, timeoutMs: 5000 };
    await expect(client.runSqlQuery(project.id, input)).resolves.toEqual(result);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/sql/query`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify(input));
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("leaves row and time limits to the server when they are omitted", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse({ columns: [], rows: [], rowCount: 0, truncated: false, durationMs: 0 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await client.runSqlQuery(project.id, { sql: "SELECT 1" });
    expect(spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify({ sql: "SELECT 1" }));
  });

  it("maps SQL validation errors to the canonical envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "The SQL statement is not allowed.",
            hint: "Only SELECT statements (optionally starting with WITH) are allowed."
          }
        },
        400
      )
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.runSqlQuery(project.id, { sql: "DELETE FROM contactos" })).rejects.toMatchObject({
      code: "VALIDATION_ERROR",
      message: "The SQL statement is not allowed.",
      hint: "Only SELECT statements (optionally starting with WITH) are allowed.",
      status: 400
    });
  });

  it("forwards an AbortSignal to the SQL query request", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse({ columns: [], rows: [], rowCount: 0, truncated: false, durationMs: 0 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await client.runSqlQuery(project.id, { sql: "SELECT pg_sleep(3)" }, controller.signal);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
  });

  it("reads the allowlisted extension catalog from the fixed Control Plane route", async () => {
    const extensions = [{
      name: "pgcrypto",
      description: "Cryptographic functions",
      trusted: true,
      availableVersion: "1.4",
      installedVersion: null,
      available: true,
      installed: false,
      canInstall: true,
      canUninstall: false
    }];
    const { fetchImpl, spy } = fetchSpy(jsonResponse(extensions));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(client.listExtensions(project.id, controller.signal)).resolves.toEqual(extensions);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/extensions`);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
  });

  it("posts only the allowlisted extension name when installing", async () => {
    const result = { name: "pgcrypto", action: "extension.install" };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(result));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.installExtension(project.id, "pgcrypto")).resolves.toEqual(result);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/extensions`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify({ name: "pgcrypto" }));
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("sends a DELETE with the extension name in the path", async () => {
    const result = { name: "uuid-ossp", action: "extension.uninstall" };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(result));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.uninstallExtension(project.id, "uuid-ossp")).resolves.toEqual(result);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/extensions/uuid-ossp`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("DELETE");
  });

  it("maps extension validation errors to the canonical envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "The PostgreSQL extension is not allowed.",
            hint: "Use a name from the extension catalog."
          }
        },
        400
      )
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.installExtension(project.id, "dblink")).rejects.toMatchObject({
      code: "VALIDATION_ERROR",
      message: "The PostgreSQL extension is not allowed.",
      hint: "Use a name from the extension catalog.",
      status: 400
    });
  });

  it("reads the migration history from the fixed Control Plane route", async () => {
    const history = [{
      version: 1,
      name: "initial_schema",
      checksum: "v1.abc123",
      operationCount: 2,
      appliedAt: "2026-11-17T12:00:00Z"
    }];
    const { fetchImpl, spy } = fetchSpy(jsonResponse(history));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(client.listMigrations(project.id, controller.signal)).resolves.toEqual(history);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/migrations`);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("posts the typed migration plan without extra fields", async () => {
    const summary = {
      version: 2,
      name: "add_contact_policy",
      checksum: "v1.def456",
      operationCount: 1,
      alreadyApplied: false
    };
    const plan = {
      version: 2,
      name: "add_contact_policy",
      operations: [{ type: "drop_table", table: "contactos" }]
    };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(summary));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.applyMigration(project.id, plan)).resolves.toEqual(summary);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/migrations`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(spy.mock.calls[0]?.[1]?.body).toBe(JSON.stringify(plan));
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("Content-Type")).toBe("application/json");
  });

  it("lists project backups from the fixed Control Plane route", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse([backup]));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(client.listBackups(project.id, controller.signal)).resolves.toEqual([backup]);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/backups`);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("creates a backup with a bodyless POST", async () => {
    const { fetchImpl, spy } = fetchSpy(jsonResponse(backup, 201));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.createBackup(project.id)).resolves.toEqual(backup);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/backups`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("POST");
    expect(spy.mock.calls[0]?.[1]?.body).toBeUndefined();
  });

  it("downloads the opaque dump with the token in a header and no user-controlled path", async () => {
    const bytes = new Uint8Array([0x50, 0x47, 0x44, 0x4d, 0x50]);
    const { fetchImpl, spy } = fetchSpy(new Response(bytes, { status: 200 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    const blob = await client.downloadBackup(project.id, backup.id, controller.signal);
    expect(blob.size).toBe(bytes.length);
    expect(spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/backups/${backup.id}/download`
    );
    const init = spy.mock.calls[0]?.[1];
    expect((init?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
    expect(init?.signal).toBe(controller.signal);
  });

  it("refuses a backup download without a session", async () => {
    const { fetchImpl, spy } = fetchSpy(new Response(new Uint8Array()));
    const client = new ControlClient({ fetch: fetchImpl });

    await expect(client.downloadBackup(project.id, backup.id)).rejects.toMatchObject({
      code: "NO_SESSION",
      status: 401
    });
    expect(spy).not.toHaveBeenCalled();
  });

  it("deletes a backup with a bodyless DELETE", async () => {
    const { fetchImpl, spy } = fetchSpy(new Response(null, { status: 204 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.deleteBackup(project.id, backup.id)).resolves.toBeUndefined();
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/backups/${backup.id}`);
    expect(spy.mock.calls[0]?.[1]?.method).toBe("DELETE");
  });

  it("sends the exact slug in the restore confirmation header", async () => {
    const result = { backupId: backup.id, projectId: project.id, status: "Ready" };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(result));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(
      client.restoreBackup(project.id, backup.id, project.slug, controller.signal)
    ).resolves.toEqual(result);
    expect(spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/backups/${backup.id}/restore`
    );
    expect(spy.mock.calls[0]?.[1]?.method).toBe("POST");
    const headers = spy.mock.calls[0]?.[1]?.headers as Headers;
    expect(headers.get("X-DomiBase-Admin-Token")).toBe("token");
    expect(headers.get("X-DomiBase-Confirm")).toBe("paneles-solares");
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
  });

  it("maps backup validation errors to the canonical envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "Confirmation does not match the project slug.",
            hint: "Send the exact project slug."
          }
        },
        400
      )
    );
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.restoreBackup(project.id, backup.id, "otro-slug")).rejects.toMatchObject({
      code: "VALIDATION_ERROR",
      message: "Confirmation does not match the project slug.",
      hint: "Send the exact project slug.",
      status: 400
    });
  });

  it("maps migration conflicts and validation errors to the canonical envelope", async () => {
    const conflict = fetchSpy(
      jsonResponse(
        { error: { code: "CONFLICT", message: "Migration version already applied." } },
        409
      )
    );
    await expect(
      new ControlClient({ token: "token", fetch: conflict.fetchImpl }).applyMigration(project.id, {
        version: 1,
        name: "initial_schema",
        operations: []
      })
    ).rejects.toMatchObject({ code: "CONFLICT", status: 409 });

    const invalid = fetchSpy(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "The migration is not valid.",
            hint: "Migration name must be a lowercase SQL-safe identifier."
          }
        },
        400
      )
    );
    await expect(
      new ControlClient({ token: "token", fetch: invalid.fetchImpl }).applyMigration(project.id, {
        version: 1,
        name: "Initial Schema",
        operations: []
      })
    ).rejects.toMatchObject({
      code: "VALIDATION_ERROR",
      status: 400,
      hint: "Migration name must be a lowercase SQL-safe identifier."
    });
  });

  it("reads audit rows with allowlisted, URL-encoded filters and the admin token", async () => {
    const page = {
      items: [{
        id: "33333333-3333-7333-8333-333333333333",
        occurredAt: "2026-11-18T11:00:00Z",
        actor: "admin",
        action: "api_key.create",
        targetType: "api_key",
        targetId: "44444444-4444-7444-8444-444444444444",
        projectId: project.id,
        details: "{\"Prefix\":\"db_public_\"}",
        correlationId: null
      }],
      total: 1,
      limit: 20,
      offset: 0
    };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(page));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(
      client.listAudit(
        {
          projectId: project.id,
          action: "api_key.create",
          from: "2026-11-18T00:00:00.000Z",
          limit: 20,
          offset: 0,
          actor: "",
          unknown: "ignored"
        } as AuditQuery,
        controller.signal
      )
    ).resolves.toEqual(page);

    const url = new URL(spy.mock.calls[0]?.[0] as string, "http://localhost");
    expect(url.pathname).toBe("/api/v1/audit");
    expect(url.searchParams.get("projectId")).toBe(project.id);
    expect(url.searchParams.get("action")).toBe("api_key.create");
    expect(url.searchParams.get("from")).toBe("2026-11-18T00:00:00.000Z");
    expect(url.searchParams.get("limit")).toBe("20");
    expect(url.searchParams.has("actor")).toBe(false);
    expect(url.searchParams.has("unknown")).toBe(false);
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("reads provisioning steps from the project-scoped Control Plane route", async () => {
    const operations = [{
      id: "55555555-5555-7555-8555-555555555555",
      projectId: project.id,
      step: "create_database",
      status: "Succeeded",
      attempt: 1,
      error: null,
      correlationId: null,
      startedAt: "2026-11-18T10:00:00Z",
      completedAt: "2026-11-18T10:00:02Z"
    }];
    const { fetchImpl, spy } = fetchSpy(jsonResponse(operations));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });
    const controller = new AbortController();

    await expect(
      client.listProvisioningOperations(project.id, controller.signal)
    ).resolves.toEqual(operations);
    expect(spy.mock.calls[0]?.[0]).toBe(
      `/api/v1/projects/${project.id}/provisioning-operations`
    );
    expect(spy.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect((spy.mock.calls[0]?.[1]?.headers as Headers).get("X-DomiBase-Admin-Token")).toBe("token");
  });

  it("maps audit validation and provisioning errors to the canonical envelope", async () => {
    const invalid = fetchSpy(
      jsonResponse({ error: { code: "VALIDATION_ERROR", message: "Parámetro desconocido." } }, 400)
    );
    await expect(
      new ControlClient({ token: "token", fetch: invalid.fetchImpl }).listAudit({ action: "x" })
    ).rejects.toMatchObject({ code: "VALIDATION_ERROR", status: 400 });

    const missing = fetchSpy(
      jsonResponse({ error: { code: "NOT_FOUND", message: "Proyecto no encontrado." } }, 404)
    );
    await expect(
      new ControlClient({ token: "token", fetch: missing.fetchImpl }).listProvisioningOperations(project.id)
    ).rejects.toMatchObject({ code: "NOT_FOUND", status: 404 });
  });

  it("exchanges owner credentials for a session token without needing an existing token", async () => {
    const session = { token: "dbsess_payload.signature", expiresAt: "2026-11-19T00:00:00Z" };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(session));
    const client = new ControlClient({ fetch: fetchImpl });

    await expect(client.login("owner", "owner-password")).resolves.toEqual(session);
    expect(spy.mock.calls[0]?.[0]).toBe("/admin/v1/login");
    const init = spy.mock.calls[0]?.[1];
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe(JSON.stringify({ username: "owner", password: "owner-password" }));
    expect((init?.headers as Headers).get("Content-Type")).toBe("application/json");
    expect((init?.headers as Headers).get("X-DomiBase-Admin-Token")).toBeNull();
    expect(client.token).toBeNull();
  });

  it("maps invalid login credentials to the canonical error envelope", async () => {
    const { fetchImpl } = fetchSpy(
      jsonResponse({ error: { code: "INVALID_CREDENTIALS", message: "Usuario o contraseña incorrectos." } }, 401)
    );
    const client = new ControlClient({ fetch: fetchImpl });

    await expect(client.login("owner", "wrong")).rejects.toMatchObject({
      code: "INVALID_CREDENTIALS",
      message: "Usuario o contraseña incorrectos.",
      status: 401
    });
  });

  it("allows overriding the login route for tests", async () => {
    const session = { token: "dbsess_x.y", expiresAt: "2026-11-19T00:00:00Z" };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(session));
    const client = new ControlClient({ baseUrl: "/custom/api/v1", loginUrl: "/custom/login", fetch: fetchImpl });

    await client.login("owner", "owner-password");
    expect(spy.mock.calls[0]?.[0]).toBe("/custom/login");
  });

  it("deletes a project with an explicit slug confirmation", async () => {
    const { fetchImpl, spy } = fetchSpy(new Response(null, { status: 204 }));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.deleteProject(project.id, project.slug)).resolves.toBeUndefined();
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}`);
    const init = spy.mock.calls[0]?.[1];
    expect(init?.method).toBe("DELETE");
    expect((init?.headers as Headers).get("X-DomiBase-Confirm-Delete")).toBe("delete");
    expect(init?.body).toBe(JSON.stringify({ confirmSlug: project.slug }));
  });

  it("reads validated column metadata from the fixed Control Plane route", async () => {
    const columns = [{
      name: "id",
      type: "int8",
      dataType: "bigint",
      nullable: false,
      default: null,
      primaryKey: true,
      unique: false,
      identity: true,
      foreignTable: null,
      foreignColumn: null
    }];
    const { fetchImpl, spy } = fetchSpy(jsonResponse(columns));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.listSchemaColumns(project.id, "contactos")).resolves.toEqual(columns);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/schema/tables/contactos/columns`);
  });

  it("sends schema mutations without SQL, roles or database names", async () => {
    const mutation = { action: "schema.table.create", table: "contactos", object: null };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(mutation));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.createTable(project.id, {
      name: "contactos",
      columns: [{ name: "id", type: "int8", primaryKey: true, identity: true }]
    })).resolves.toEqual(mutation);
    expect(spy.mock.calls[0]?.[0]).toBe(`/api/v1/projects/${project.id}/schema/tables`);
    expect(spy.mock.calls[0]?.[1]?.body).not.toContain("DROP");
  });

  it("bounds row edits to validated filters and never forwards role or database", async () => {
    const edit = { affected: 1, rows: [{ id: 1 }] };
    const { fetchImpl, spy } = fetchSpy(jsonResponse(edit));
    const client = new ControlClient({ token: "token", fetch: fetchImpl });

    await expect(client.updateRows(project.id, "contactos", { email: "a@b.test" }, ["id.eq.1"])).resolves.toEqual(edit);
    const url = new URL(spy.mock.calls[0]?.[0] as string, "http://localhost");
    expect(url.pathname).toBe(`/api/v1/projects/${project.id}/data/tables/contactos/rows`);
    expect(url.searchParams.getAll("f")).toEqual(["id.eq.1"]);
    expect(url.searchParams.has("role")).toBe(false);
    expect(url.searchParams.has("database")).toBe(false);
    expect(url.searchParams.has("apikey")).toBe(false);
  });

  it("exposes the public API URL without credentials", () => {
    const client = new ControlClient({ baseUrl: "http://127.0.0.1:5080/api/v1" });

    expect(client.apiUrl).toBe("http://127.0.0.1:5080");
    expect(JSON.stringify(client)).not.toContain("secret");
  });
});
