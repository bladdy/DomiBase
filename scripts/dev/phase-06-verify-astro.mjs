#!/usr/bin/env node
/**
 * Verifies the Astro example through its server-rendered forms.
 *
 * Expects `npm run dev` (or `npm run start`) to be running and the .env produced by
 * scripts/dev/phase-06-demo.mjs. Performs INSERT, UPDATE and DELETE through the HTML
 * form endpoints, checks the rendered pages, and inspects /diagnostico.
 *
 * Usage: node scripts/dev/phase-06-verify-astro.mjs [baseUrl]
 */

import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");
const exampleDirectory = join(repositoryRoot, "examples", "astro-example");
const exampleEnvPath = join(exampleDirectory, ".env");
const sdkEntry = join(repositoryRoot, "packages", "client", "dist", "index.js");

if (!existsSync(exampleEnvPath)) {
  fail("Falta examples/astro-example/.env. Ejecuta node scripts/dev/phase-06-demo.mjs.");
}

if (!existsSync(sdkEntry)) {
  fail("Falta packages/client/dist. Ejecuta cmd /c npm --prefix packages/client run build.");
}

loadDotEnv(exampleEnvPath);

const baseUrl = (process.argv[2] ?? process.env.ASTRO_EXAMPLE_URL ?? "http://localhost:4325").replace(
  /\/+$/,
  ""
);
const apiUrl = (process.env.DOMIBASE_API_URL ?? "http://localhost:5080").replace(/\/+$/, "");
const projectKey = process.env.DOMIBASE_PROJECT_KEY;
const projectName = process.env.DOMIBASE_PROJECT_NAME ?? "Paneles Solares";

if (!projectKey) {
  fail("DOMIBASE_PROJECT_KEY no está definida en examples/astro-example/.env.");
}

const { createClient } = await import(pathToFileURL(sdkEntry).href);
const primary = createClient({ url: apiUrl, projectKey });

const runId = Date.now();
const email = `astro+${runId}@example.test`;
const nombre = `Contacto Astro ${runId}`;
let createdId = null;
let failures = 0;

try {
  const home = await fetch(`${baseUrl}/`);
  assert(home.status === 200, `GET / devolvió HTTP ${home.status}.`);
  const homeHtml = await home.text();
  assert(homeHtml.includes(projectName), "La portada no muestra el nombre del proyecto.");
  assert(homeHtml.includes("Nuevo contacto"), "La portada no muestra el formulario de alta.");
  assert(!homeHtml.includes("db_public_"), "La portada incluye una clave pública en el HTML.");
  assertHeader(home, "cache-control", "no-store");
  assertHeader(home, "x-content-type-options", "nosniff");
  assertHeader(home, "x-frame-options", "DENY");

  const csp = home.headers.get("content-security-policy");
  if (csp) {
    assert(csp.includes("default-src 'self'"), "La CSP no incluye default-src 'self'.");
    assert(csp.includes("frame-ancestors 'none'"), "La CSP no bloquea el framing.");
  }

  pass("Portada SSR y cabeceras de seguridad");

  const created = await postForm({
    intent: "create",
    nombre,
    email,
    estado: "nuevo",
    notas: "Creado desde el formulario de Astro."
  });
  await assertRedirect(created, "ok=created");

  const createdRow = await primary
    .from("contactos")
    .select("id,email,estado")
    .eq("email", email)
    .single();
  assert(!createdRow.error && createdRow.data?.email === email, "El INSERT de Astro no llegó a PostgreSQL.");
  createdId = createdRow.data.id;

  const afterCreate = await fetch(`${baseUrl}/?ok=created`);
  const afterCreateHtml = await afterCreate.text();
  assert(afterCreateHtml.includes(nombre), "La fila creada no aparece en la página.");
  assert(afterCreateHtml.includes(email), "El correo creado no aparece en la página.");
  pass("INSERT desde el formulario Astro visible en el listado");

  const updated = await postForm({ intent: "update", id: createdId, estado: "cliente" });
  await assertRedirect(updated, "ok=updated");

  const updatedRow = await primary
    .from("contactos")
    .select("id,estado")
    .eq("id", createdId)
    .single();
  assert(
    !updatedRow.error && updatedRow.data?.estado === "cliente",
    "El UPDATE del formulario no persistió el estado."
  );

  const afterUpdate = await fetch(`${baseUrl}/?ok=updated`);
  const afterUpdateHtml = await afterUpdate.text();
  assert(afterUpdateHtml.includes(nombre), "La fila actualizada ya no aparece.");
  assert(afterUpdateHtml.includes("status-cliente"), "La página no refleja el estado 'cliente'.");
  pass("UPDATE desde el formulario Astro persistido y renderizado");

  const deleted = await postForm({ intent: "delete", id: createdId });
  await assertRedirect(deleted, "ok=deleted");
  createdId = null;

  const afterDelete = await fetch(`${baseUrl}/?ok=deleted`);
  const afterDeleteHtml = await afterDelete.text();
  assert(!afterDeleteHtml.includes(email), "La fila eliminada sigue apareciendo en la página.");

  const gone = await primary.from("contactos").select("id").eq("email", email);
  assert(!gone.error && (gone.data?.length ?? 0) === 0, "La fila eliminada sigue en PostgreSQL.");
  pass("DELETE desde el formulario Astro persistido y retirado del listado");

  const diagnostics = await fetch(`${baseUrl}/diagnostico`);
  assert(diagnostics.status === 200, `GET /diagnostico devolvió HTTP ${diagnostics.status}.`);
  const diagnosticsHtml = await diagnostics.text();
  assert(
    !diagnosticsHtml.includes("db_public_"),
    "La página de diagnóstico incluye una clave pública en el HTML."
  );

  const statuses = new Map();
  for (const match of diagnosticsHtml.matchAll(/data-check="([^"]+)" data-status="([^"]+)"/g)) {
    statuses.set(match[1], match[2]);
  }

  const required = [
    "primary-read",
    "secondary-read",
    "project-isolation",
    "metadata-isolation",
    "invalid-key",
    "revoked-key",
    "missing-key"
  ];

  for (const id of required) {
    assert(statuses.get(id) === "pass", `La comprobación '${id}' no pasó (${statuses.get(id) ?? "ausente"}).`);
  }

  for (const [id, status] of statuses) {
    assert(status !== "fail", `La comprobación '${id}' falló.`);
  }

  pass(`Diagnóstico en vivo: ${required.length} comprobaciones en OK`);
} catch (error) {
  failures += 1;
  fail(error instanceof Error ? error.message : String(error));
} finally {
  if (createdId) {
    const cleanup = await primary.from("contactos").delete().eq("id", createdId).select("id");
    if (!cleanup.error && (cleanup.data?.length ?? 0) === 1) {
      console.log("  [OK] Fila de prueba limpiada tras el fallo");
    }
  }
}

if (failures > 0) {
  process.exitCode = 1;
} else {
  console.log("\nVerificación Astro completada sin fallos.");
}

async function postForm(fields) {
  const body = new URLSearchParams(fields);
  return fetch(`${baseUrl}/`, {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
      Origin: baseUrl
    },
    body,
    redirect: "manual"
  });
}

async function assertRedirect(response, expectedQuery) {
  assert(
    response.status === 303,
    `El formulario devolvió HTTP ${response.status} en lugar de 303: ${(await response.text()).slice(0, 400)}`
  );

  const location = response.headers.get("location");
  assert(typeof location === "string", "La redirección no incluye Location.");

  const target = new URL(location, baseUrl);
  assert(
    target.pathname === "/" && target.search.includes(expectedQuery),
    `Redirección inesperada: ${target.pathname}${target.search}`
  );
}

function assertHeader(response, name, expected) {
  const actual = response.headers.get(name);
  assert(actual === expected, `Cabecera ${name}: se esperaba '${expected}' y llegó '${actual}'.`);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function pass(message) {
  console.log(`  [OK] ${message}`);
}

function fail(message) {
  console.error(`  [FALLO] ${message}`);
  process.exit(1);
}

function loadDotEnv(path) {
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
