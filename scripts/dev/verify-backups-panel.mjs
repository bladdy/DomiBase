#!/usr/bin/env node
/**
 * End-to-end verification of logical backups from the real local stack.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running with the EF migration applied.
 * - DomiBase API at DOMIBASE_API_URL (default http://127.0.0.1:5080), started with
 *   BACKUP_ROOT, BACKUP_TOOL_MODE=docker and BACKUP_DOCKER_CONTAINER=domibase-postgres.
 * - Admin panel dev server at ADMIN_PANEL_URL (default http://127.0.0.1:5190).
 * - DOMIBASE_ADMIN_TOKEN in the environment or the repository `.env`.
 * - Playwright installed in frontend/admin and Chrome available as channel `chrome`.
 *
 * The script provisions one temporary project, exposes a table through the Control Plane,
 * inserts a row through the public Runtime, then drives create/download/restore/delete from
 * the panel. It verifies that the restore actually reverted the data. No token, API key,
 * password or dump content is printed.
 *
 * Usage: node scripts/dev/verify-backups-panel.mjs
 */

import { existsSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");

loadDotEnv(join(repositoryRoot, ".env"));

const apiUrl = (process.env.DOMIBASE_API_URL ?? "http://127.0.0.1:5080").replace(/\/+$/, "");
const panelUrl = (process.env.ADMIN_PANEL_URL ?? "http://127.0.0.1:5190").replace(/\/+$/, "");
const adminToken = process.env.DOMIBASE_ADMIN_TOKEN;
const suffix = Date.now().toString(36).slice(-8);
const slug = `bkp-e2e-${suffix}`;
const table = "backup_e2e";

let passed = 0;
let projectId = null;
let browser = null;
let publicKey = null;

main().catch((error) => {
  console.error(`\n[FALLO] ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}).finally(async () => {
  if (projectId) {
    try {
      const remaining = await api(`/api/v1/projects/${projectId}/backups`);
      for (const backup of remaining ?? []) {
        try {
          await api(`/api/v1/projects/${projectId}/backups/${backup.id}`, { method: "DELETE" });
        } catch {
          // Best effort: the project deletion below is the authoritative cleanup.
        }
      }
      await deleteProject(projectId, slug);
      pass("Proyecto temporal y copias eliminados");
    } catch (error) {
      console.error(`[LIMPIEZA] No se pudo eliminar el proyecto temporal: ${message(error)}`);
      process.exitCode = 1;
    }
  }
  if (browser) {
    await browser.close();
  }
  console.log(`\nComprobaciones superadas: ${passed} · fallos: ${process.exitCode ? 1 : 0}`);
});

async function main() {
  assert(Boolean(adminToken), "Falta DOMIBASE_ADMIN_TOKEN en el entorno o en .env.");
  await waitForApi();

  const requireFromAdmin = createRequire(join(repositoryRoot, "frontend", "admin", "package.json"));
  const { chromium } = requireFromAdmin("playwright");

  console.log("Backups y restauración · verificación E2E del panel");

  const created = await api("/api/v1/projects", {
    method: "POST",
    body: JSON.stringify({ slug, name: "Backup E2E" })
  });
  assert(created.status === "Ready", `El proyecto quedó en ${created.status}.`);
  projectId = created.id;
  pass("Proyecto temporal provisionado");

  await api(`/api/v1/projects/${projectId}/schema/tables`, {
    method: "POST",
    body: JSON.stringify({
      name: table,
      columns: [
        { name: "id", type: "int4", nullable: false, primaryKey: true },
        { name: "note", type: "text", nullable: false }
      ]
    })
  });
  await api(`/api/v1/projects/${projectId}/schema/tables/${table}/policies`, {
    method: "POST",
    body: JSON.stringify({
      name: "anon_select_insert",
      commands: ["SELECT", "INSERT"],
      roles: ["anon"],
      using: "true",
      withCheck: "true"
    })
  });
  pass("Tabla expuesta con política RLS de SELECT e INSERT");

  const key = await api(`/api/v1/projects/${projectId}/keys`, {
    method: "POST",
    body: JSON.stringify({ label: "backup-e2e" })
  });
  assert(typeof key.key === "string" && key.key.length > 0, "La API no devolvió una clave pública.");
  publicKey = key.key;
  pass("Clave pública emitida para el Runtime");

  const inserted = await runtime("POST", { id: 1, note: "original" });
  assert(inserted.status === 201, `El insert del Runtime devolvió ${inserted.status}.`);
  assert((await selectRows()).length === 1, "La fila inicial no está presente.");
  pass("Fila original insertada y comprobada vía Runtime");

  browser = await chromium.launch({ channel: "chrome", headless: true });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    acceptDownloads: true
  });
  const page = await context.newPage();
  const consoleErrors = [];
  const failedResponses = [];
  page.on("console", (entry) => {
    if (entry.type() === "error") {
      consoleErrors.push(entry.text());
    }
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("response", (response) => {
    if (response.status() >= 400) {
      failedResponses.push(`${response.status()} ${response.url()}`);
    }
  });

  await page.goto(panelUrl, { waitUntil: "networkidle" });
  await page.locator("#admin-token").fill(adminToken);
  await page.getByRole("button", { name: "Abrir panel" }).click();
  await page.locator("#panel-projects").waitFor({ state: "visible" });
  pass("Sesión administrativa abierta en el panel");

  await page.locator("#tab-backups").click();
  const panel = page.locator("#panel-backups");
  await panel.waitFor({ state: "visible" });
  assert((await page.locator("#tab-backups").getAttribute("aria-controls")) === "panel-backups");
  assert((await page.locator("h1").innerText()).includes("Backups"));
  pass("Pestaña Backups accesible con su panel y encabezado");

  await panel.getByText("Todavía no hay copias para este proyecto.").waitFor({ state: "visible" });
  pass("Estado vacío mostrado antes de crear copias");

  await panel.getByRole("button", { name: "Crear backup" }).click();
  const row = panel.locator("tbody tr").first();
  await row.waitFor({ state: "visible" });
  assert((await panel.locator("thead th").count()) === 5, "La tabla no tiene cinco encabezados.");
  assert((await row.locator("th[scope=row]").count()) === 1, "La fila no tiene encabezado de fila.");
  assert((await panel.locator("caption").count()) === 1, "La tabla no tiene caption accesible.");
  pass("Copia creada y tabla semántica con encabezados");

  const backups = await api(`/api/v1/projects/${projectId}/backups`);
  assert(backups.length === 1, `La Control API devolvió ${backups.length} copias.`);
  assert(backups[0].format === "custom", "El formato del volcado no es custom.");
  assert(backups[0].sha256.length === 64, "La huella SHA-256 no tiene 64 caracteres.");
  assert(backups[0].sizeBytes > 0, "El volcado está vacío.");
  pass("Control API confirma formato, tamaño y huella del volcado");

  const [download] = await Promise.all([
    page.waitForEvent("download"),
    row.getByRole("button", { name: /Descargar backup/ }).click()
  ]);
  assert(download.suggestedFilename().endsWith(".dump"), "La descarga no usa extensión .dump.");
  const dumpPath = await download.path();
  assert(dumpPath && readFileSync(dumpPath).subarray(0, 5).toString("ascii") === "PGDMP");
  pass("Descarga real del volcado pg_dump desde el panel");

  const mutated = await runtime("POST", { id: 2, note: "after-backup" });
  assert(mutated.status === 201, `El insert posterior devolvió ${mutated.status}.`);
  assert((await selectRows()).length === 2, "La mutación no se reflejó en el Runtime.");
  pass("Datos mutados después del volcado");

  await row.getByRole("button", { name: /Restaurar backup/ }).click();
  const restoreDialog = page.getByRole("dialog", { name: "Restaurar proyecto" });
  await restoreDialog.waitFor({ state: "visible" });
  assert((await restoreDialog.getAttribute("aria-labelledby")) !== null);
  const confirmRestore = restoreDialog.getByRole("button", { name: "Restaurar", exact: true });
  assert(await confirmRestore.isDisabled(), "La confirmación no empieza deshabilitada.");
  await restoreDialog.locator("#restore-confirm-slug").fill("slug-incorrecto");
  assert(await confirmRestore.isDisabled(), "Un slug incorrecto habilitó la confirmación.");
  await restoreDialog.locator("#restore-confirm-slug").fill(slug);
  assert(await confirmRestore.isEnabled(), "El slug correcto no habilitó la confirmación.");
  await page.keyboard.press("Escape");
  await restoreDialog.waitFor({ state: "hidden" });
  pass("Diálogo nativo bloquea la restauración hasta escribir el slug exacto");

  await row.getByRole("button", { name: /Restaurar backup/ }).click();
  await restoreDialog.waitFor({ state: "visible" });
  await restoreDialog.locator("#restore-confirm-slug").fill(slug);
  await confirmRestore.click();
  await restoreDialog.waitFor({ state: "hidden" });
  await waitForStatus("Ready");
  const restoredRows = await selectRows();
  assert(restoredRows.length === 1, `Tras restaurar hay ${restoredRows.length} filas.`);
  assert(restoredRows[0].note === "original", "La restauración no recuperó la fila original.");
  pass("Restauración real: vuelve la fila original y desaparece la posterior");

  await panel.locator("tbody tr").first().getByRole("button", { name: /Eliminar backup/ }).click();
  const deleteDialog = page.getByRole("dialog", { name: "Eliminar backup" });
  await deleteDialog.waitFor({ state: "visible" });
  await deleteDialog.getByRole("button", { name: "Eliminar", exact: true }).click();
  await deleteDialog.waitFor({ state: "hidden" });
  await panel.getByText("Todavía no hay copias para este proyecto.").waitFor({ state: "visible" });
  assert((await api(`/api/v1/projects/${projectId}/backups`)).length === 0);
  pass("Copia eliminada desde el panel y confirmado por la Control API");

  await page.setViewportSize({ width: 390, height: 844 });
  const overflow = await page.evaluate(() => {
    const documentElement = document.documentElement;
    const offenders = [...document.querySelectorAll("body *")]
      .map((element) => {
        const rect = element.getBoundingClientRect();
        return {
          element: `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ""}`,
          right: Math.round(rect.right),
          scrollWidth: element.scrollWidth,
          clientWidth: element.clientWidth,
          inTabs: Boolean(element.closest(".tabs")),
          inTableScroller: Boolean(element.closest(".table-wrap"))
        };
      })
      .filter((item) => !item.inTabs && !item.inTableScroller)
      .filter((item) => item.right > window.innerWidth + 1 || item.scrollWidth > item.clientWidth + 1)
      .slice(0, 8);
    return {
      overflow: documentElement.scrollWidth - documentElement.clientWidth,
      innerWidth: window.innerWidth,
      offenders
    };
  });
  assert(overflow.overflow <= 1, `Desbordamiento horizontal en móvil: ${JSON.stringify(overflow)}`);
  await page.screenshot({
    path: join(repositoryRoot, ".impeccable", "review", "backups-mobile.png"),
    fullPage: true
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.screenshot({
    path: join(repositoryRoot, ".impeccable", "review", "backups-desktop.png"),
    fullPage: true
  });
  pass("Sin desbordamiento en 390×844; capturas guardadas");

  assert((await page.locator("h1").count()) === 1, "El panel no tiene exactamente un h1.");
  assert(
    consoleErrors.length === 0,
    `Errores de consola: ${consoleErrors.slice(0, 2).join(" | ")}; respuestas: ${failedResponses.slice(0, 4).join(" | ")}`
  );
  assert(failedResponses.length === 0, `Respuestas fallidas: ${failedResponses.slice(0, 4).join(" | ")}`);
  pass("Sin errores de consola ni respuestas 4xx/5xx en el panel");
}

async function api(path, init = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    ...init,
    headers: {
      "X-DomiBase-Admin-Token": adminToken,
      "Content-Type": "application/json",
      ...(init.headers ?? {})
    }
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
    throw new Error(payload?.error?.message ?? `HTTP ${response.status} en ${path}`);
  }
  return payload;
}

async function runtime(method, body) {
  const response = await fetch(`${apiUrl}/rest/v1/${table}`, {
    method,
    headers: {
      apikey: publicKey,
      "Content-Type": "application/json"
    },
    body: body ? JSON.stringify(body) : undefined
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
  return { status: response.status, payload };
}

async function selectRows() {
  const result = await runtime("GET");
  assert(result.status === 200, `El SELECT del Runtime devolvió ${result.status}.`);
  return result.payload?.data ?? [];
}

async function waitForStatus(expected) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const project = await api(`/api/v1/projects/${projectId}`);
    if (project.status === expected) {
      return;
    }
    await sleep(200);
  }
  throw new Error(`El proyecto no volvió a ${expected} tras la restauración.`);
}

async function waitForApi() {
  for (let attempt = 1; attempt <= 30; attempt += 1) {
    try {
      const response = await fetch(`${apiUrl}/health/ready`);
      if (response.ok) {
        pass(`API lista en ${apiUrl}`);
        return;
      }
    } catch {
      // API still starting.
    }
    if (attempt === 30) {
      throw new Error(`La API no responde en ${apiUrl}/health/ready.`);
    }
    await sleep(1000);
  }
}

async function deleteProject(id, confirmSlug) {
  await api(`/api/v1/projects/${id}`, {
    method: "DELETE",
    headers: { "X-DomiBase-Confirm-Delete": "delete" },
    body: JSON.stringify({ confirmSlug })
  });
}

function loadDotEnv(path) {
  if (!existsSync(path)) {
    return;
  }
  for (const rawLine of readFileSync(path, "utf8").split(/\r?\n/)) {
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
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    if (process.env[name] === undefined) {
      process.env[name] = value;
    }
  }
}

function assert(condition, errorMessage) {
  if (!condition) {
    throw new Error(errorMessage);
  }
}

function pass(notice) {
  passed += 1;
  console.log(`  [OK] ${notice}`);
}

function message(error) {
  return error instanceof Error ? error.message : String(error);
}

function sleep(milliseconds) {
  return new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}
