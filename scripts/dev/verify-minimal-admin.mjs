#!/usr/bin/env node
/**
 * End-to-end verification of the minimal DomiBase flow against the real API and PostgreSQL.
 *
 * Prerequisites: PostgreSQL in Docker, the API running from the repository .env, and the SDK built.
 * The script never prints credentials or key values. It writes only non-secret markers to
 * scripts/dev/.minimal-e2e-state.json so a second run can prove persistence after a restart.
 *
 * Usage: node scripts/dev/verify-minimal-admin.mjs [--after-restart]
 */

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");
const envPath = join(repositoryRoot, ".env");
const statePath = join(scriptDirectory, ".minimal-e2e-state.json");
const sdkEntry = join(repositoryRoot, "packages", "client", "dist", "index.js");
const afterRestart = process.argv.includes("--after-restart");

if (!existsSync(envPath)) {
  fail("Falta el .env local de la API.");
}
if (!existsSync(sdkEntry)) {
  fail("Falta packages/client/dist. Ejecuta npm --prefix packages/client run build.");
}

const env = loadDotEnv(envPath);
const apiUrl = (env.ASPNETCORE_URLS ?? "http://127.0.0.1:5080")
  .split(";")[0]
  .replace("*", "127.0.0.1")
  .replace(/\/+$/, "");

const username = env.DOMIBASE_ADMIN_USERNAME;
const password = env.DOMIBASE_ADMIN_PASSWORD;
if (!username || !password) {
  fail("Faltan DOMIBASE_ADMIN_USERNAME o DOMIBASE_ADMIN_PASSWORD en .env.");
}

const { createClient } = await import(pathToFileURL(sdkEntry).href);
const runId = Date.now();
const checks = [];
let sessionToken;
let temporaryProjectId;

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms));

function fail(message) {
  console.error(`FAIL ${message}`);
  process.exit(1);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function loadDotEnv(path) {
  const result = {};
  for (const rawLine of readFileSync(path, "utf8").split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line === "" || line.startsWith("#")) {
      continue;
    }
    const separator = line.indexOf("=");
    if (separator < 1) {
      continue;
    }
    const key = line.slice(0, separator).trim();
    let value = line.slice(separator + 1).trim();
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    result[key] = value;
  }
  return result;
}

async function call(path, options = {}) {
  const headers = { ...(options.headers ?? {}) };
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (sessionToken && options.control !== false) {
    headers["X-DomiBase-Admin-Token"] = sessionToken;
  }

  const response = await fetch(`${apiUrl}${path}`, {
    method: options.method ?? "GET",
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: AbortSignal.timeout(20_000)
  });

  const text = await response.text();
  let body = null;
  if (text !== "") {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }
  return { status: response.status, body };
}

async function check(name, action) {
  try {
    const result = await action();
    checks.push({ name, ok: true });
    console.log(`ok   ${name}`);
    return result;
  } catch (error) {
    checks.push({ name, ok: false, error: error.message });
    console.error(`FAIL ${name}: ${error.message}`);
    throw error;
  }
}

async function waitForReady(projectId) {
  for (let attempt = 0; attempt < 40; attempt += 1) {
    const response = await call(`/api/v1/projects/${projectId}`);
    if (response.status === 200 && response.body?.status === "Ready") {
      return response.body;
    }
    if (response.status === 200 && response.body?.status === "Failed") {
      throw new Error(`El proyecto quedó en Failed: ${response.body.failureReason ?? "sin motivo"}`);
    }
    await sleep(500);
  }
  throw new Error("El provisioning no alcanzó el estado Ready.");
}

async function createPublicKey(projectId, label) {
  const response = await call(`/api/v1/projects/${projectId}/keys`, {
    method: "POST",
    body: { kind: "Public", label, expiresAt: null }
  });
  assert(response.status === 201 && response.body?.key?.startsWith("db_public_"),
    `No se pudo emitir la clave pública (HTTP ${response.status}).`);
  return { id: response.body.id, value: response.body.key };
}

async function sdkSelect(projectKey, filters = [], table = "contactos") {
  let query = createClient({ url: apiUrl, projectKey }).from(table).select("id,nombre,email,estado");
  for (const filter of filters) {
    query = query.eq(filter.column, filter.value);
  }
  return query;
}

async function ensureTable(projectId) {
  const listed = await call(`/api/v1/projects/${projectId}/schema/tables`);
  assert(listed.status === 200, `No se pudo listar el esquema (HTTP ${listed.status}).`);
  if (listed.body.some((table) => table.name === "contactos")) {
    return;
  }

  const created = await call(`/api/v1/projects/${projectId}/schema/tables`, {
    method: "POST",
    body: {
      name: "contactos",
      columns: [
        { name: "id", type: "uuid", nullable: false, primaryKey: true, default: "gen_random_uuid()" },
        { name: "nombre", type: "text", nullable: false },
        { name: "email", type: "text", nullable: true },
        { name: "estado", type: "text", nullable: false, default: "nuevo" },
        { name: "created_at", type: "timestamptz", nullable: false, default: "now()" }
      ]
    }
  });
  assert(created.status === 200 || created.status === 201,
    `No se pudo crear contactos (HTTP ${created.status}).`);
}

async function ensurePolicy(projectId, command) {
  const name = `anon_${command.toLowerCase()}`;
  const listed = await call(`/api/v1/projects/${projectId}/schema/tables/contactos/policies`);
  assert(listed.status === 200, `No se pudieron listar políticas (HTTP ${listed.status}).`);
  if (listed.body.some((policy) => policy.name === name)) {
    return;
  }

  const created = await call(`/api/v1/projects/${projectId}/schema/tables/contactos/policies`, {
    method: "POST",
    body: {
      name,
      commands: [command],
      roles: ["anon"],
      using: "true",
      withCheck: "true"
    }
  });
  assert(created.status === 200 || created.status === 201,
    `No se pudo crear ${name} (HTTP ${created.status}).`);
}

async function main() {
  console.log(`Verificación mínima contra ${apiUrl}${afterRestart ? " (post-reinicio)" : ""}`);

  await check("Login del propietario", async () => {
    const response = await call("/admin/v1/login", {
      method: "POST",
      control: false,
      body: { username, password }
    });
    assert(response.status === 200 && response.body?.token?.startsWith("dbsess_"),
      `Login rechazado (HTTP ${response.status}).`);
    sessionToken = response.body.token;
  });

  await check("La sesión abre la Control API", async () => {
    const response = await call("/api/v1/projects");
    assert(response.status === 200 && Array.isArray(response.body),
      `La sesión no autorizó /api/v1/projects (HTTP ${response.status}).`);
  });

  let project;
  await check("Proyecto paneles-solares Ready", async () => {
    const listed = await call("/api/v1/projects");
    project = listed.body.find((item) => item.slug === "paneles-solares");
    if (!project) {
      const created = await call("/api/v1/projects", {
        method: "POST",
        body: { slug: "paneles-solares", name: "Paneles Solares" }
      });
      assert(created.status === 201, `No se pudo crear el proyecto (HTTP ${created.status}).`);
      project = created.body;
    }
    project = await waitForReady(project.id);
    assert(project.databaseName === "domibase_paneles_solares",
      "La base asignada no es la esperada.");
  });

  await check("Tabla contactos con columnas reales", async () => {
    await ensureTable(project.id);
    const columns = await call(`/api/v1/projects/${project.id}/schema/tables/contactos/columns`);
    assert(columns.status === 200, `No se pudieron leer columnas (HTTP ${columns.status}).`);
    const names = columns.body.map((column) => column.name);
    for (const expected of ["id", "nombre", "email", "estado", "created_at"]) {
      assert(names.includes(expected), `Falta la columna ${expected}.`);
    }
  });

  await check("Políticas RLS anon_select y anon_insert", async () => {
    await ensurePolicy(project.id, "SELECT");
    await ensurePolicy(project.id, "INSERT");
    const listed = await call(`/api/v1/projects/${project.id}/schema/tables/contactos/policies`);
    const names = listed.body.map((policy) => policy.name);
    assert(names.includes("anon_select") && names.includes("anon_insert"),
      "Las políticas complementarias no están presentes.");
  });

  const issued = await check("Clave pública emitida para el SDK", async () => {
    return createPublicKey(project.id, `e2e-minimal-${runId}`);
  });
  const key = issued.value;

  const sdkEmail = `sdk+${runId}@example.test`;
  await check("Insert y lectura desde el SDK con RLS", async () => {
    const inserted = await createClient({ url: apiUrl, projectKey: key })
      .from("contactos")
      .insert({ nombre: `SDK ${runId}`, email: sdkEmail, estado: "nuevo" });
    assert(!inserted.error && inserted.data?.length === 1,
      `INSERT del SDK rechazado: ${inserted.error?.code ?? "sin datos"}`);

    const selected = await sdkSelect(key, [{ column: "email", value: sdkEmail }]);
    assert(!selected.error && selected.data?.length === 1,
      `SELECT del SDK no devolvió la fila: ${selected.error?.code ?? "sin datos"}`);
  });

  const editorEmail = `editor+${runId}@example.test`;
  await check("Escritura del Table Editor por Control Plane", async () => {
    const edited = await call(`/api/v1/projects/${project.id}/data/tables/contactos/rows`, {
      method: "POST",
      body: { nombre: `Editor ${runId}`, email: editorEmail, estado: "nuevo" }
    });
    assert(edited.status === 200 && edited.body?.affected === 1,
      `El editor no insertó la fila (HTTP ${edited.status}).`);

    const selected = await sdkSelect(key, [{ column: "email", value: editorEmail }]);
    assert(!selected.error && selected.data?.length === 1,
      "La fila del editor no es visible por el SDK con RLS.");
  });

  if (afterRestart && existsSync(statePath)) {
    const previous = JSON.parse(readFileSync(statePath, "utf8"));
    await check("Persistencia de filas tras reiniciar PostgreSQL", async () => {
      for (const email of previous.emails) {
        const selected = await sdkSelect(key, [{ column: "email", value: email }]);
        assert(!selected.error && selected.data?.length === 1,
          `La fila ${email} no sobrevivió al reinicio.`);
      }
    });
  }

  await check("Clave inválida rechazada", async () => {
    const selected = await sdkSelect(`${key}-invalida`);
    assert(selected.error !== null && selected.data === null,
      "Una clave inválida devolvió datos.");
  });

  await check("SQL injection en identificador rechazada", async () => {
    const injected = await createClient({ url: apiUrl, projectKey: key })
      .from("contactos; DROP TABLE usuarios")
      .select("id");
    assert(injected.error !== null, "El SDK aceptó un identificador inyectado.");
  });

  await check("Tabla sin política RLS no expone filas al visitante", async () => {
    const privateTable = "privado_e2e";
    const tables = await call(`/api/v1/projects/${project.id}/schema/tables`);
    if (!tables.body.some((table) => table.name === privateTable)) {
      const created = await call(`/api/v1/projects/${project.id}/schema/tables`, {
        method: "POST",
        body: {
          name: privateTable,
          columns: [
            { name: "id", type: "uuid", nullable: false, primaryKey: true, default: "gen_random_uuid()" },
            { name: "secreto", type: "text", nullable: false }
          ]
        }
      });
      assert(created.status === 200 || created.status === 201,
        `No se pudo crear ${privateTable} (HTTP ${created.status}).`);
    }

    const policy = await call(`/api/v1/projects/${project.id}/schema/tables/${privateTable}/policies`, {
      method: "POST",
      body: {
        name: "anon_temporal",
        commands: ["SELECT", "INSERT"],
        roles: ["anon"],
        using: "true",
        withCheck: "true"
      }
    });
    assert(policy.status === 200 || policy.status === 201,
      `No se pudo crear la política temporal (HTTP ${policy.status}).`);

    const inserted = await call(`/api/v1/projects/${project.id}/data/tables/${privateTable}/rows`, {
      method: "POST",
      body: { secreto: `secreto-${runId}` }
    });
    assert(inserted.status === 200 && inserted.body?.affected === 1,
      `No se pudo insertar en ${privateTable} (HTTP ${inserted.status}).`);

    const dropped = await call(
      `/api/v1/projects/${project.id}/schema/tables/${privateTable}/policies/anon_temporal`,
      { method: "DELETE" });
    assert(dropped.status === 200, `No se pudo retirar la política (HTTP ${dropped.status}).`);

    const blocked = await createClient({ url: apiUrl, projectKey: key })
      .from(privateTable)
      .select("id,secreto");
    assert(blocked.error !== null || blocked.data?.length === 0,
      `RLS no bloqueó la lectura: ${JSON.stringify(blocked.data)}`);

    const removed = await call(`/api/v1/projects/${project.id}/schema/tables/${privateTable}`, {
      method: "DELETE"
    });
    assert(removed.status === 200, `No se pudo borrar ${privateTable} (HTTP ${removed.status}).`);
  });

  await check("Otro proyecto no puede leer contactos", async () => {
    const created = await call("/api/v1/projects", {
      method: "POST",
      body: {
        slug: `aislamiento-minimo-${runId}`,
        name: "Aislamiento mínimo"
      }
    });
    assert(created.status === 201, `No se pudo crear el proyecto de aislamiento (HTTP ${created.status}).`);
    temporaryProjectId = created.body.id;
    await waitForReady(temporaryProjectId);

    const other = await createPublicKey(temporaryProjectId, `aislamiento-${runId}`);
    const foreign = await sdkSelect(other.value);
    assert(foreign.error !== null && foreign.data === null,
      "El segundo proyecto leyó la tabla del primero.");
  });

  await check("Borrado controlado del proyecto temporal", async () => {
    const temporary = await call(`/api/v1/projects/${temporaryProjectId}`);
    const response = await call(`/api/v1/projects/${temporaryProjectId}`, {
      method: "DELETE",
      headers: { "X-DomiBase-Confirm-Delete": "delete" },
      body: { confirmSlug: temporary.body.slug }
    });
    assert(response.status === 204, `No se pudo borrar el proyecto temporal (HTTP ${response.status}).`);
    temporaryProjectId = undefined;
  });

  const email = await check("Clave de prueba revocada", async () => {
    const response = await call(`/api/v1/projects/${project.id}/keys/${issued.id}`, {
      method: "DELETE"
    });
    assert(response.status === 204, `No se pudo revocar la clave (HTTP ${response.status}).`);
    return sdkEmail;
  });

  const previousEmails = afterRestart && existsSync(statePath)
    ? JSON.parse(readFileSync(statePath, "utf8")).emails
    : [];
  writeFileSync(statePath, `${JSON.stringify({
    projectId: project.id,
    slug: project.slug,
    emails: [...new Set([...previousEmails, email, editorEmail])],
    updatedAt: new Date().toISOString()
  }, null, 2)}\n`, "utf8");

  console.log(`\n${checks.length}/${checks.length} comprobaciones correctas.`);
}

main().catch(() => process.exit(1));
