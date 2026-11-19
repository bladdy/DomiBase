#!/usr/bin/env node
/**
 * End-to-end verification of the Migrations tab against the real local stack.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running.
 * - DomiBase API at DOMIBASE_API_URL (default http://127.0.0.1:5080).
 * - Admin panel dev server at ADMIN_PANEL_URL (default http://127.0.0.1:5190).
 * - DOMIBASE_ADMIN_TOKEN in the environment or the repository `.env`.
 * - Playwright installed in frontend/admin and Chrome available as channel `chrome`.
 *
 * The script creates one temporary project, drives the UI with Playwright, verifies the
 * Control API history and deletes the project in `finally`. It never prints the admin token.
 *
 * Usage: node scripts/dev/verify-migrations-panel.mjs
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
const slug = `mig-e2e-${Date.now().toString(36).slice(-8)}`;

let passed = 0;
let projectId = null;
let browser = null;

main().catch((error) => {
  console.error(`\n[FALLO] ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}).finally(async () => {
  if (projectId) {
    try {
      await deleteProject(projectId, slug);
      pass("Proyecto temporal eliminado");
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

  console.log("Migraciones · verificación E2E del panel");
  const created = await api("/api/v1/projects", {
    method: "POST",
    body: JSON.stringify({ slug, name: "Migraciones E2E" })
  });
  projectId = created.id;
  assert(created.status === "Ready", `El proyecto temporal quedó en ${created.status}.`);
  pass(`Proyecto temporal listo (${slug})`);

  browser = await chromium.launch({ channel: "chrome", headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const consoleErrors = [];
  const failedResponses = [];
  let conflictInFlight = false;
  page.on("console", (entry) => {
    if (entry.type() !== "error") {
      return;
    }
    const text = entry.text();
    if (conflictInFlight && (text.includes("409 (Conflict)") || text.includes("400 (Bad Request)"))) {
      return;
    }
    consoleErrors.push(text);
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("response", (response) => {
    if (response.status() < 400) {
      return;
    }
    if (
      conflictInFlight &&
      (response.status() === 409 || response.status() === 400) &&
      response.url().includes("/migrations")
    ) {
      return;
    }
    failedResponses.push(`${response.status()} ${response.url()}`);
  });

  await page.goto(panelUrl, { waitUntil: "networkidle" });
  await page.locator("#admin-token").fill(adminToken);
  await page.getByRole("button", { name: "Abrir panel" }).click();
  await page.locator("#panel-projects").waitFor({ state: "visible" });
  pass("Sesión administrativa abierta en el panel");

  await page.keyboard.press("Tab");
  const outline = await page.evaluate(() => {
    const style = getComputedStyle(document.activeElement ?? document.body);
    return { style: style.outlineStyle, width: style.outlineWidth };
  });
  assert(outline.style !== "none" && outline.width !== "0px", "El foco de teclado no muestra indicador visible.");
  pass("Indicador :focus-visible presente");

  await page.locator("#tab-migrations").click();
  await page.locator("#active-project").selectOption(projectId);
  await page.locator("#migration-history-heading").waitFor({ state: "visible" });
  await page.waitForFunction(() => document.querySelector("#migration-version")?.value === "1");
  pass("Pestaña Migraciones cargada con versión sugerida 1");

  await page.locator("#migration-name").fill("initial_schema");
  await page.locator("#migration-op-0-name").fill("contactos");
  await page.locator("#migration-op-0-col-0-name").fill("id");
  await page.locator("#migration-op-0-col-0-type").selectOption("uuid");
  await page.locator("#migration-op-0-col-0-default-mode").selectOption("gen_random_uuid()");
  const primaryColumn = page.locator(".migration-column").first();
  await primaryColumn.getByLabel("Obligatoria").check();
  await primaryColumn.getByLabel("Clave primaria").check();

  await page.locator("#migration-new-type").selectOption("create_policy");
  await page.getByRole("button", { name: "Añadir", exact: true }).click();
  await page.locator("#migration-op-1-table").fill("contactos");
  await page.locator("#migration-op-1-name").fill("contactos_select");
  pass("Plan tipado construido en el panel");

  const preview = await page.locator(".migration-preview").innerText();
  assert(preview.includes('"version": 1'), "La vista previa no muestra la versión 1.");
  assert(preview.includes('"type": "create_table"'), "La vista previa no incluye create_table.");
  assert(preview.includes('"type": "create_policy"'), "La vista previa no incluye create_policy.");
  assert(!preview.includes("DROP TABLE") && !preview.includes("CREATE TABLE"), "La vista previa contiene SQL.");
  pass("Vista previa JSON válida y sin SQL");

  await page.getByRole("button", { name: "Revisar y aplicar" }).click();
  const dialog = page.getByRole("dialog", { name: "Aplicar migración" });
  await dialog.waitFor({ state: "visible" });
  assert((await dialog.getAttribute("aria-labelledby")) !== null, "El diálogo no tiene aria-labelledby.");
  await dialog.getByRole("button", { name: "Aplicar migración" }).click();
  await page.getByText("Migración aplicada", { exact: true }).waitFor({ state: "visible", timeout: 20_000 });
  pass("Migración aplicada desde el panel");

  let history = await api(`/api/v1/projects/${projectId}/migrations`);
  assert(history.length === 1, `El historial tiene ${history.length} filas; se esperaba 1.`);
  const applied = history[0];
  assert(applied.version === 1 && applied.name === "initial_schema", "Versión/nombre incorrectos en el historial.");
  assert(applied.operationCount === 2, `El historial registra ${applied.operationCount} operaciones; se esperaban 2.`);
  assert(String(applied.checksum).startsWith("v1."), "El checksum no tiene el prefijo v1.");
  pass("Historial Control API verificado (checksum y operaciones)");

  await page.locator("#panel-migrations tbody tr").filter({ hasText: "initial_schema" }).waitFor({ state: "visible" });
  assert((await page.locator("#panel-migrations thead th").count()) >= 5, "La tabla no conserva su semántica.");
  pass("Fila de historial y tabla semántica visibles");

  await page.locator("#migration-version").fill("1");
  await page.getByRole("button", { name: "Revisar y aplicar" }).click();
  await dialog.getByRole("button", { name: "Aplicar migración" }).click();
  await page.getByText("Migración ya aplicada", { exact: true }).waitFor({ state: "visible", timeout: 20_000 });
  history = await api(`/api/v1/projects/${projectId}/migrations`);
  assert(history.length === 1, "La reaplicación idéntica duplicó el historial.");
  pass("Reaplicación idéntica → alreadyApplied sin nueva fila");

  await page.locator("#migration-version").fill("1");
  await page.locator("#migration-name").fill("initial_schema_v2");
  await page.getByRole("button", { name: "Revisar y aplicar" }).click();
  conflictInFlight = true;
  await dialog.getByRole("button", { name: "Aplicar migración" }).click();
  await page.getByText("La migración no se pudo aplicar", { exact: true }).waitFor({ state: "visible", timeout: 20_000 });
  conflictInFlight = false;
  history = await api(`/api/v1/projects/${projectId}/migrations`);
  assert(history.length === 1, "Una versión en conflicto modificó el historial.");
  pass("Versión modificada → conflicto sin cambios en el historial");

  await page.setViewportSize({ width: 390, height: 844 });
  const overflowReport = await page.evaluate(() => {
    const doc = document.documentElement;
    const offenders = [...document.querySelectorAll("body *")]
      .map((element) => {
        const rect = element.getBoundingClientRect();
        return {
          element: `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ""}${element.className && typeof element.className === "string" ? `.${element.className.trim().split(/\s+/).join(".")}` : ""}`,
          right: Math.round(rect.right),
          width: Math.round(rect.width),
          scrollWidth: element.scrollWidth,
          clientWidth: element.clientWidth,
          inTabs: Boolean(element.closest(".tabs")),
          inTableScroller: Boolean(element.closest(".table-wrap"))
        };
      })
      .filter((item) => !item.inTabs && !item.inTableScroller)
      .filter((item) => item.right > window.innerWidth + 1 || item.scrollWidth > item.clientWidth + 1)
      .sort((a, b) => b.right - a.right)
      .slice(0, 8);
    const describe = (selector) => {
      const start = document.querySelector(selector);
      const chain = [];
      for (let node = start; node && node !== doc; node = node.parentElement) {
        const style = getComputedStyle(node);
        chain.push({
          element: `${node.tagName.toLowerCase()}${node.id ? `#${node.id}` : ""}${node.className && typeof node.className === "string" ? `.${node.className.trim().split(/\s+/).join(".")}` : ""}`,
          width: Math.round(node.getBoundingClientRect().width),
          scrollWidth: node.scrollWidth,
          clientWidth: node.clientWidth,
          minWidth: style.minWidth,
          display: style.display,
          gridColumns: style.gridTemplateColumns
        });
      }
      return chain;
    };
    const column = document.querySelector("#panel-migrations .migration-column");
    const columnChildren = [...(column?.children ?? [])].map((node) => {
      const rect = node.getBoundingClientRect();
      return {
        element: `${node.tagName.toLowerCase()}${node.className && typeof node.className === "string" ? `.${node.className.trim().split(/\s+/).join(".")}` : ""}`,
        left: Math.round(rect.left),
        right: Math.round(rect.right),
        width: Math.round(rect.width),
        scrollWidth: node.scrollWidth,
        clientWidth: node.clientWidth
      };
    });
    return {
      overflow: doc.scrollWidth - doc.clientWidth,
      innerWidth: window.innerWidth,
      offenders,
      ancestors: describe("#panel-migrations .migration-column"),
      columnChildren
    };
  });
  assert(
    overflowReport.overflow <= 1,
    `Desbordamiento horizontal en móvil: ${overflowReport.overflow}px. ` +
      JSON.stringify({
        offenders: overflowReport.offenders,
        ancestors: overflowReport.ancestors,
        columnChildren: overflowReport.columnChildren
      })
  );
  await page.screenshot({ path: join(repositoryRoot, ".impeccable", "review", "migrations-mobile.png"), fullPage: true });

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.screenshot({ path: join(repositoryRoot, ".impeccable", "review", "migrations-desktop.png"), fullPage: true });
  pass("Sin desbordamiento en 390×844; capturas guardadas");

  assert((await page.locator("h1").count()) === 1, "El panel no tiene exactamente un h1.");
  assert(
    consoleErrors.length === 0,
    `Errores de consola: ${consoleErrors.slice(0, 2).join(" | ")}; respuestas: ${failedResponses.slice(0, 4).join(" | ")}`
  );
  assert(failedResponses.length === 0, `Respuestas fallidas: ${failedResponses.slice(0, 4).join(" | ")}`);
  pass("Sin errores de consola ni respuestas 4xx/5xx");
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

function headers(extra = {}) {
  return {
    "X-DomiBase-Admin-Token": adminToken,
    ...extra
  };
}

async function api(path, init = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    ...init,
    headers: headers({ "Content-Type": "application/json", ...(init.headers ?? {}) })
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

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function pass(message) {
  passed += 1;
  console.log(`  [OK] ${message}`);
}

function message(error) {
  return error instanceof Error ? error.message : String(error);
}

function sleep(milliseconds) {
  return new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}
