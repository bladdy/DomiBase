#!/usr/bin/env node
/**
 * Phase 7 validation.
 *
 * Verifies, against the real local stack (PostgreSQL in Docker + DomiBase API),
 * that the demo projects survive a container restart and that the Runtime still
 * enforces isolation and key security. It is read-only: it never creates,
 * rotates or revokes keys.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running.
 * - DomiBase API running at DOMIBASE_API_URL (default http://localhost:5080).
 * - examples/astro-example/.env written by scripts/dev/phase-06-demo.mjs.
 *
 * Usage: node scripts/dev/phase-07-validate.mjs
 */

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");
const exampleEnvPath = join(repositoryRoot, "examples", "astro-example", ".env");
const sdkEntry = join(repositoryRoot, "packages", "client", "dist", "index.js");

loadDotEnv(join(repositoryRoot, ".env"));
loadDotEnv(exampleEnvPath);

const apiUrl = (process.env.DOMIBASE_API_URL ?? "http://localhost:5080").replace(/\/+$/, "");
const primaryKey = process.env.DOMIBASE_PROJECT_KEY;
const secondaryKey = process.env.DOMIBASE_PROJECT_KEY_B ?? null;
const revokedKey = process.env.DOMIBASE_REVOKED_PROJECT_KEY ?? null;
const postgresUser = process.env.POSTGRES_USER;
const postgresContainer = process.env.DOMIBASE_POSTGRES_CONTAINER ?? "domibase-postgres";

const PRIMARY_SEED_EMAIL = "contacto@paneles-solares.test";
const SECONDARY_SEED_EMAIL = "contacto@aislamiento-demo.test";
const PRIMARY_DB = "domibase_paneles_solares";
const SECONDARY_DB = "domibase_aislamiento_demo";

let passed = 0;
let failures = 0;

main().catch((error) => {
  failures += 1;
  console.error(`\n[FALLO] ${error instanceof Error ? error.message : String(error)}`);
  printSummary();
  process.exitCode = 1;
});

async function main() {
  if (!existsSync(sdkEntry)) {
    throw new Error(
      "No existe packages/client/dist. Ejecuta: cmd /c npm --prefix packages/client run build"
    );
  }

  if (!primaryKey) {
    throw new Error("Falta DOMIBASE_PROJECT_KEY en examples/astro-example/.env.");
  }

  if (!postgresUser) {
    throw new Error("Falta POSTGRES_USER en el .env raíz.");
  }

  console.log("Fase 7 · validación post-reinicio");
  await waitForApi();

  const { createClient } = await import(pathToFileURL(sdkEntry).href);
  const primary = createClient({ url: apiUrl, projectKey: primaryKey });

  await verifyPrimaryIsolation(primary);
  await verifySecondaryIsolation(createClient);
  await verifyMetadataIsolation(primary);
  await verifyRejectedKeys({ createClient });
  verifyPostgresResources();

  printSummary();
  if (failures > 0) {
    process.exitCode = 1;
  }
}

async function waitForApi() {
  const attempts = 40;

  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      const response = await fetch(`${apiUrl}/health/ready`);
      if (response.ok) {
        pass(`API lista en ${apiUrl}`);
        return;
      }
    } catch {
      // API not started yet.
    }

    if (attempt === attempts) {
      throw new Error(`La API no responde en ${apiUrl}/health/ready.`);
    }

    await sleep(1000);
  }
}

async function verifyPrimaryIsolation(primary) {
  const own = await primary
    .from("contactos")
    .select("id,email")
    .eq("email", PRIMARY_SEED_EMAIL);
  assert(!own.error, `El proyecto principal no leyó su fila: ${formatError(own.error)}`);
  assert(
    (own.data?.length ?? 0) === 1,
    `El proyecto principal no encontró su fila semilla (${own.data?.length ?? 0} filas).`
  );

  const foreign = await primary
    .from("contactos")
    .select("id")
    .eq("email", SECONDARY_SEED_EMAIL);
  assert(
    !foreign.error && (foreign.data?.length ?? 0) === 0,
    "El proyecto principal pudo leer la fila del segundo proyecto."
  );

  pass("Aislamiento del proyecto principal (ve su fila, no la ajena)");
}

async function verifySecondaryIsolation(createClient) {
  if (!secondaryKey) {
    console.log("  [OMITIDO] Sin DOMIBASE_PROJECT_KEY_B; no se comprueba el segundo proyecto.");
    return;
  }

  const secondary = createClient({ url: apiUrl, projectKey: secondaryKey });

  const own = await secondary
    .from("contactos")
    .select("id,email")
    .eq("email", SECONDARY_SEED_EMAIL);
  assert(!own.error, `El segundo proyecto no leyó su fila: ${formatError(own.error)}`);
  assert(
    (own.data?.length ?? 0) === 1,
    `El segundo proyecto no encontró su fila semilla (${own.data?.length ?? 0} filas).`
  );

  const foreign = await secondary
    .from("contactos")
    .select("id")
    .eq("email", PRIMARY_SEED_EMAIL);
  assert(
    !foreign.error && (foreign.data?.length ?? 0) === 0,
    "El segundo proyecto pudo leer la fila del proyecto principal."
  );

  pass("Aislamiento bidireccional entre proyectos");
}

async function verifyMetadataIsolation(primary) {
  const metadata = await primary.from("projects").select("id").limit(1);
  assert(
    metadata.error?.code === "TABLE_NOT_FOUND",
    "La base de metadatos fue accesible desde el Runtime."
  );

  pass("domibase_control bloqueado desde el Runtime (404 TABLE_NOT_FOUND)");
}

async function verifyRejectedKeys({ createClient }) {
  const invalid = createClient({
    url: apiUrl,
    projectKey: "db_public_invalid_phase07_demo_key"
  });
  const invalidResult = await invalid.from("contactos").select("id").limit(1);
  assert(
    invalidResult.error?.code === "INVALID_API_KEY" && invalidResult.error.status === 401,
    "Una clave inválida no fue rechazada con 401 INVALID_API_KEY."
  );

  if (revokedKey) {
    const revoked = createClient({ url: apiUrl, projectKey: revokedKey });
    const revokedResult = await revoked.from("contactos").select("id").limit(1);
    assert(
      revokedResult.error?.code === "INVALID_API_KEY" && revokedResult.error.status === 401,
      "Una clave revocada no fue rechazada con 401 INVALID_API_KEY."
    );
  } else {
    console.log("  [OMITIDO] Sin DOMIBASE_REVOKED_PROJECT_KEY; no se comprueba la revocación.");
  }

  const missingResponse = await fetch(`${apiUrl}/rest/v1/contactos?select=id&limit=1`);
  const missingBody = await missingResponse.json().catch(() => null);
  assert(
    missingResponse.status === 401 && missingBody?.error?.code === "MISSING_API_KEY",
    "Una petición sin API key no fue rechazada con 401 MISSING_API_KEY."
  );

  pass("Clave inválida, clave revocada y clave ausente rechazadas con 401");
}

function verifyPostgresResources() {
  const databases = queryPostgres(
    "postgres",
    "SELECT datname FROM pg_database WHERE datname LIKE 'domibase%' ORDER BY datname;"
  );
  for (const database of [PRIMARY_DB, SECONDARY_DB, "domibase_control"]) {
    assert(
      databases.includes(database),
      `No existe la base PostgreSQL esperada: ${database}.`
    );
  }

  const roles = queryPostgres(
    "postgres",
    "SELECT rolname FROM pg_roles WHERE rolname LIKE 'domibase_%' ORDER BY rolname;"
  );
  for (const suffix of ["_owner", "_runtime", "_anon"]) {
    assert(
      roles.includes(`${PRIMARY_DB}${suffix}`) && roles.includes(`${SECONDARY_DB}${suffix}`),
      `Faltan roles PostgreSQL esperados para el sufijo ${suffix}.`
    );
  }

  pass(`Bases y roles presentes (${databases.length} bases, ${roles.length} roles domibase_*)`);

  const primaryRows = queryPostgres(
    PRIMARY_DB,
    "SELECT email FROM public.contactos ORDER BY email;"
  );
  assert(
    primaryRows.includes(PRIMARY_SEED_EMAIL),
    "La fila semilla de paneles-solares no sobrevivió al reinicio."
  );

  const secondaryRows = queryPostgres(
    SECONDARY_DB,
    "SELECT email FROM public.contactos ORDER BY email;"
  );
  assert(
    secondaryRows.includes(SECONDARY_SEED_EMAIL),
    "La fila semilla de aislamiento-demo no sobrevivió al reinicio."
  );

  pass(`Persistencia de filas verificada (${primaryRows.length} y ${secondaryRows.length} filas semilla)`);
}

function queryPostgres(database, sql) {
  const result = spawnSync(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-U",
      postgresUser,
      "-d",
      database,
      "-t",
      "-A",
      "-c",
      sql
    ],
    { encoding: "utf8" }
  );

  if (result.status !== 0) {
    const detail = (result.stderr || result.stdout || "sin salida").trim().split("\n").slice(0, 2).join(" ");
    throw new Error(`No se pudo consultar PostgreSQL (${database}): ${detail}`);
  }

  return result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
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

function printSummary() {
  console.log(`\nComprobaciones superadas: ${passed} · fallos: ${failures}`);
}

function formatError(error) {
  return error ? `${error.code}: ${error.message}` : "sin error";
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
