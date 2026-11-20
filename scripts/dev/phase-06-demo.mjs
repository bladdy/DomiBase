#!/usr/bin/env node
/**
 * Phase 6 demo orchestrator.
 *
 * Creates (or reuses) two DomiBase projects, provisions their public keys and
 * demo schema, writes the Astro example environment file, and verifies the SDK
 * against the real Runtime API. It never prints API key values or .env secrets.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running.
 * - DomiBase API running at DOMIBASE_API_URL (default http://localhost:5080).
 *
 * Usage: node scripts/dev/phase-06-demo.mjs
 */

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");
const exampleDirectory = join(repositoryRoot, "examples", "astro-example");
const exampleEnvPath = join(exampleDirectory, ".env");
const sdkEntry = join(repositoryRoot, "packages", "client", "dist", "index.js");

loadDotEnv(join(repositoryRoot, ".env"));

const apiUrl = (process.env.DOMIBASE_API_URL ?? "http://localhost:5080").replace(/\/+$/, "");
const adminToken = requireEnv("DOMIBASE_ADMIN_TOKEN");
const postgresUser = requireEnv("POSTGRES_USER");
const postgresContainer = process.env.DOMIBASE_POSTGRES_CONTAINER ?? "domibase-postgres";

const PRIMARY = {
  slug: "paneles-solares",
  name: "Paneles Solares",
  seed: {
    nombre: "Contacto Paneles Solares",
    email: "contacto@paneles-solares.test",
    estado: "nuevo",
    notas: "Fila sembrada por el script de demo de la Fase 6."
  }
};

const SECONDARY = {
  slug: "aislamiento-demo",
  name: "Aislamiento Demo",
  seed: {
    nombre: "Contacto Aislamiento",
    email: "contacto@aislamiento-demo.test",
    estado: "contactado",
    notas: "Fila exclusiva del segundo proyecto."
  }
};

let passed = 0;

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`\n[FALLO] ${message}`);
  process.exitCode = 1;
});

async function main() {
  if (!existsSync(sdkEntry)) {
    throw new Error(
      "No existe packages/client/dist. Ejecuta primero: cmd /c npm --prefix packages/client run build"
    );
  }

  console.log("Fase 6 · preparación de la demo Astro");
  await waitForApi();

  const primaryProject = await ensureProject(PRIMARY);
  const secondaryProject = await ensureProject(SECONDARY);
  pass(`Proyectos Ready: ${PRIMARY.slug}, ${SECONDARY.slug}`);

  await revokePreviousDemoKeys(primaryProject.id);
  await revokePreviousDemoKeys(secondaryProject.id);

  const primaryKey = await createKey(primaryProject.id, "phase-06-demo-primary");
  const secondaryKey = await createKey(secondaryProject.id, "phase-06-demo-secondary");
  const revokedKey = await createKey(primaryProject.id, "phase-06-invalid-revoked");
  await revokeKey(primaryProject.id, revokedKey.id);
  pass("Claves públicas creadas y clave de prueba revocada");

  createDemoSchema(primaryProject, PRIMARY.seed);
  createDemoSchema(secondaryProject, SECONDARY.seed);
  pass("Tablas contactos con RLS, GRANT y filas de demo preparadas");

  const { createClient } = await import(pathToFileURL(sdkEntry).href);
  const primary = createClient({ url: apiUrl, projectKey: primaryKey.key });
  const secondary = createClient({ url: apiUrl, projectKey: secondaryKey.key });

  await verifyCrud(primary);
  await verifyIsolation(primary, secondary);
  await verifyRejectedKeys({ createClient, revokedKey });
  pass("CRUD, aislamiento y rechazo de claves verificados con @domibase/client");

  writeExampleEnv({
    primaryKey: primaryKey.key,
    secondaryKey: secondaryKey.key,
    revokedKey: revokedKey.key
  });
  pass("examples/astro-example/.env escrito (sin imprimir valores)");

  console.log("\nDemo lista.");
  console.log(`  Runtime: ${apiUrl}`);
  console.log(`  Proyectos: ${PRIMARY.slug} + ${SECONDARY.slug}`);
  console.log(`  Comprobaciones SDK superadas: ${passed}`);
  console.log("  Siguiente: cd examples/astro-example; cmd /c npm install; cmd /c npm run dev");
}

async function waitForApi() {
  const attempts = 40;

  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      const response = await fetch(`${apiUrl}/health/ready`);
      if (response.ok) {
        const body = await response.json().catch(() => null);
        if (body?.status === "ready" || body?.status === "Healthy" || body?.status === "healthy") {
          pass(`API lista en ${apiUrl}`);
          return;
        }

        if (response.status === 200) {
          pass(`API responde /health/ready en ${apiUrl}`);
          return;
        }
      }
    } catch {
      // API not started yet.
    }

    if (attempt === attempts) {
      throw new Error(
        `La API no responde en ${apiUrl}/health/ready. Arráncala con: dotnet run --project backend/src/DomiBase.Api`
      );
    }

    await sleep(1000);
  }
}

async function ensureProject(definition) {
  const existing = (await admin("GET", "/api/v1/projects")).find(
    (project) => project.slug === definition.slug
  );

  let project = existing;

  if (!project) {
    try {
      project = await admin("POST", "/api/v1/projects", {
        slug: definition.slug,
        name: definition.name
      });
    } catch (error) {
      // A concurrent run or a stale local list can surface a 409; re-read once.
      const projects = await admin("GET", "/api/v1/projects");
      project = projects.find((candidate) => candidate.slug === definition.slug);
      if (!project) {
        throw error;
      }
    }
  }

  if (project.status === "Ready") {
    return project;
  }

  if (project.status === "Failed") {
    project = await admin("POST", `/api/v1/projects/${project.id}/provision`);
  }

  for (let attempt = 0; attempt < 30; attempt += 1) {
    const current = await admin("GET", `/api/v1/projects/${project.id}`);
    if (current.status === "Ready") {
      return current;
    }

    if (current.status === "Failed") {
      throw new Error(
        `El provisioning de '${definition.slug}' falló (${current.failureReason ?? "sin motivo"}).`
      );
    }

    await sleep(1000);
    project = current;
  }

  throw new Error(`El proyecto '${definition.slug}' no alcanzó el estado Ready.`);
}

async function revokePreviousDemoKeys(projectId) {
  const keys = await admin("GET", `/api/v1/projects/${projectId}/keys`);

  for (const key of keys) {
    if (
      !key.revokedAt &&
      typeof key.label === "string" &&
      (key.label.startsWith("phase-06-demo-") || key.label.startsWith("phase-06-invalid-"))
    ) {
      await revokeKey(projectId, key.id);
    }
  }
}

async function createKey(projectId, label) {
  return admin("POST", `/api/v1/projects/${projectId}/keys`, { kind: "public", label });
}

async function revokeKey(projectId, keyId) {
  await admin("DELETE", `/api/v1/projects/${projectId}/keys/${keyId}`);
}

async function verifyCrud(primary) {
  const email = `demo+${Date.now()}@example.test`;

  const created = await primary
    .from("contactos")
    .insert({
      nombre: "Contacto de verificación Fase 6",
      email,
      estado: "nuevo",
      notas: "Fila temporal creada por el orquestador."
    })
    .select("id,nombre,email,estado,notas")
    .single();

  assert(!created.error, `INSERT falló: ${formatError(created.error)}`);
  assert(typeof created.data?.id === "string", "INSERT no devolvió el id generado.");
  const id = created.data.id;

  const read = await primary
    .from("contactos")
    .select("id,nombre,email,estado")
    .eq("id", id)
    .single();
  assert(!read.error && read.data?.email === email, "SELECT por id no devolvió la fila insertada.");

  const updated = await primary
    .from("contactos")
    .update({ estado: "contactado", notas: "Actualizado por phase-06-demo.mjs" })
    .eq("id", id)
    .select("id,estado,notas")
    .single();
  assert(
    !updated.error && updated.data?.estado === "contactado",
    "UPDATE no persistió el nuevo estado."
  );

  const deleted = await primary
    .from("contactos")
    .delete()
    .eq("id", id)
    .select("id");
  assert(!deleted.error && (deleted.data?.length ?? 0) === 1, "DELETE no devolvió la fila eliminada.");

  const afterDelete = await primary.from("contactos").select("id").eq("id", id);
  assert(
    !afterDelete.error && (afterDelete.data?.length ?? 0) === 0,
    "La fila seguía existiendo tras DELETE."
  );

  pass("CRUD completo (INSERT → SELECT → UPDATE → DELETE) verificado");
}

async function verifyIsolation(primary, secondary) {
  const secondarySeed = await secondary
    .from("contactos")
    .select("id,email")
    .eq("email", SECONDARY.seed.email)
    .single();
  assert(
    !secondarySeed.error && secondarySeed.data?.email === SECONDARY.seed.email,
    "El segundo proyecto no ve su propia fila sembrada."
  );

  const primaryCross = await primary
    .from("contactos")
    .select("id,email")
    .eq("email", SECONDARY.seed.email);
  assert(
    !primaryCross.error && (primaryCross.data?.length ?? 0) === 0,
    "El proyecto principal pudo leer datos del segundo proyecto."
  );

  const secondaryCross = await secondary
    .from("contactos")
    .select("id,email")
    .eq("email", PRIMARY.seed.email);
  assert(
    !secondaryCross.error && (secondaryCross.data?.length ?? 0) === 0,
    "El segundo proyecto pudo leer datos del proyecto principal."
  );

  const metadata = await primary.from("projects").select("id").limit(1);
  assert(
    metadata.error?.code === "TABLE_NOT_FOUND",
    "La base de metadatos fue accesible desde el Runtime."
  );

  pass("Aislamiento bidireccional y bloqueo de domibase_control verificados");
}

async function verifyRejectedKeys({ createClient, revokedKey }) {
  const invalid = createClient({
    url: apiUrl,
    projectKey: "db_public_invalid_phase06_demo_key"
  });
  const invalidResult = await invalid.from("contactos").select("id").limit(1);
  assert(
    invalidResult.error?.code === "INVALID_API_KEY" && invalidResult.error.status === 401,
    "Una clave inválida no fue rechazada con 401 INVALID_API_KEY."
  );

  const revoked = createClient({ url: apiUrl, projectKey: revokedKey.key });
  const revokedResult = await revoked.from("contactos").select("id").limit(1);
  assert(
    revokedResult.error?.code === "INVALID_API_KEY" && revokedResult.error.status === 401,
    "Una clave revocada no fue rechazada con 401 INVALID_API_KEY."
  );

  const missingResponse = await fetch(`${apiUrl}/rest/v1/contactos?select=id&limit=1`);
  const missingBody = await missingResponse.json().catch(() => null);
  assert(
    missingResponse.status === 401 && missingBody?.error?.code === "MISSING_API_KEY",
    "Una petición sin API key no fue rechazada con 401 MISSING_API_KEY."
  );

  pass("Clave inválida, clave revocada y clave ausente rechazadas con 401");
}

function createDemoSchema(project, seed) {
  const databaseName = `domibase_${project.slug.replaceAll("-", "_")}`;
  const ownerRole = `${databaseName}_owner`;
  const anonymousRole = `${databaseName}_anon`;

  assertIdentifier(databaseName, ownerRole, anonymousRole);
  assertSingleQuoteFree(seed.nombre, seed.email, seed.estado, seed.notas);

  const sql = `
SET ROLE "${ownerRole}";

CREATE TABLE IF NOT EXISTS public.contactos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    nombre text NOT NULL,
    email text UNIQUE,
    estado text NOT NULL DEFAULT 'nuevo',
    notas text,
    created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.contactos ENABLE ROW LEVEL SECURITY;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.contactos TO "${anonymousRole}";

DROP POLICY IF EXISTS contactos_select ON public.contactos;
CREATE POLICY contactos_select ON public.contactos FOR SELECT TO "${anonymousRole}" USING (true);

DROP POLICY IF EXISTS contactos_insert ON public.contactos;
CREATE POLICY contactos_insert ON public.contactos FOR INSERT TO "${anonymousRole}" WITH CHECK (true);

DROP POLICY IF EXISTS contactos_update ON public.contactos;
CREATE POLICY contactos_update ON public.contactos FOR UPDATE TO "${anonymousRole}" USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS contactos_delete ON public.contactos;
CREATE POLICY contactos_delete ON public.contactos FOR DELETE TO "${anonymousRole}" USING (true);

INSERT INTO public.contactos (nombre, email, estado, notas)
VALUES ('${seed.nombre}', '${seed.email}', '${seed.estado}', '${seed.notas}')
ON CONFLICT (email) DO NOTHING;

RESET ROLE;
`;

  const result = spawnSync(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      postgresUser,
      "-d",
      databaseName
    ],
    { input: sql, encoding: "utf8" }
  );

  if (result.status !== 0) {
    const detail = (result.stderr || result.stdout || "sin salida").trim().split("\n").slice(0, 3).join(" ");
    throw new Error(`No se pudo preparar el esquema de '${project.slug}': ${detail}`);
  }
}

function writeExampleEnv({ primaryKey, secondaryKey, revokedKey }) {
  const quote = (value) => `"${String(value).replaceAll('"', '\\"')}"`;
  const content = [
    "# Generated by scripts/dev/phase-06-demo.mjs. Do not commit this file.",
    "# Server-only values: never rename these with the PUBLIC_ prefix.",
    `DOMIBASE_API_URL=${apiUrl}`,
    `DOMIBASE_PROJECT_KEY=${primaryKey}`,
    `DOMIBASE_PROJECT_KEY_B=${secondaryKey}`,
    `DOMIBASE_REVOKED_PROJECT_KEY=${revokedKey}`,
    `DOMIBASE_PROJECT_NAME=${quote(PRIMARY.name)}`,
    `DOMIBASE_PROJECT_NAME_B=${quote(SECONDARY.name)}`,
    ""
  ].join("\n");

  writeFileSync(exampleEnvPath, content, { encoding: "utf8" });
}

async function admin(method, path, body) {
  const headers = {
    "X-DomiBase-Admin-Token": adminToken,
    Accept: "application/json"
  };

  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const response = await fetch(`${apiUrl}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  let payload = null;

  if (text.length > 0) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = null;
    }
  }

  if (!response.ok) {
    const code = payload?.error?.code;
    throw new Error(
      `Control ${method} ${path} falló (HTTP ${response.status}${code ? ` ${code}` : ""}).`
    );
  }

  return payload;
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function pass(message) {
  passed += 1;
  console.log(`  [OK] ${message}`);
}

function formatError(error) {
  return error ? `${error.code}: ${error.message}` : "sin error";
}

function assertIdentifier(...identifiers) {
  for (const identifier of identifiers) {
    if (!/^[a-z0-9_]+$/.test(identifier)) {
      throw new Error("Identificador PostgreSQL inesperado.");
    }
  }
}

function assertSingleQuoteFree(...values) {
  for (const value of values) {
    if (value.includes("'")) {
      throw new Error("Un valor de demo contiene una comilla simple inesperada.");
    }
  }
}

function requireEnv(name) {
  const value = process.env[name];
  if (!value) {
    throw new Error(`Falta la variable de entorno ${name}.`);
  }

  return value;
}

function loadDotEnv(path) {
  if (!existsSync(path)) {
    return;
  }

  const content = readFileSync(path, "utf8");

  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line.length === 0 || line.startsWith("#")) {
      continue;
    }

    const separator = line.indexOf("=");
    if (separator <= 0) {
      continue;
    }

    const name = line.slice(0, separator).trim();
    let value = line.slice(separator + 1).trim();

    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }

    if (process.env[name] === undefined) {
      process.env[name] = value;
    }
  }
}

function sleep(milliseconds) {
  return new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}
