#!/usr/bin/env node
/**
 * End-to-end verification of per-project rate limits against the real local stack.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running with the EF migration applied.
 * - DomiBase API at DOMIBASE_API_URL (default http://127.0.0.1:5080).
 * - Admin panel dev server at ADMIN_PANEL_URL (default http://127.0.0.1:5190).
 * - DOMIBASE_ADMIN_TOKEN in the environment or the repository `.env`.
 * - Playwright installed in frontend/admin and Chrome available as channel `chrome`.
 *
 * The script creates two temporary projects, edits their quotas from the panel, verifies the
 * Control API and the real /rest/v1 behavior, then deletes both projects. It never prints the
 * admin token or API key values.
 *
 * Usage: node scripts/dev/verify-rate-limits-panel.mjs
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
const slugs = [`rl-e2e-a-${suffix}`, `rl-e2e-b-${suffix}`];

let passed = 0;
const projectIds = [];
let browser = null;

main().catch((error) => {
  console.error(`\n[FALLO] ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}).finally(async () => {
  for (const [index, projectId] of projectIds.entries()) {
    try {
      await deleteProject(projectId, slugs[index]);
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

  console.log("Rate limit por proyecto · verificación E2E del panel");
  for (const [index, slug] of slugs.entries()) {
    const created = await api("/api/v1/projects", {
      method: "POST",
      body: JSON.stringify({ slug, name: `Rate limit E2E ${index === 0 ? "A" : "B"}` })
    });
    assert(created.status === "Ready", `El proyecto ${slug} quedó en ${created.status}.`);
    projectIds.push(created.id);
  }
  pass("Dos proyectos temporales provisionados");

  browser = await chromium.launch({ channel: "chrome", headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
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

  await page.locator("#tab-projects").click();
  for (const slug of slugs) {
    await projectRow(page, slug).waitFor({ state: "visible" });
  }
  assert((await page.locator("#panel-projects thead th").allTextContents()).some((text) => text.includes("Cupo")));
  assert((await page.locator("#panel-projects table thead th").count()) >= 5);
  assert((await page.locator("#panel-projects table tbody tr").count()) >= 2);
  pass("Tabla semántica con columna Cupo y filas de los proyectos");

  await editLimit(page, slugs[0], 2, 60);
  await editLimit(page, slugs[1], 5, 60);
  pass("Cupos 2/60 y 5/60 guardados desde el diálogo nativo");

  const first = await api(`/api/v1/projects/${projectIds[0]}`);
  const second = await api(`/api/v1/projects/${projectIds[1]}`);
  assert(first.rateLimitPermits === 2 && first.effectiveRateLimitPermits === 2);
  assert(second.rateLimitPermits === 5 && second.effectiveRateLimitPermits === 5);
  pass("Control API confirma los overrides y los valores efectivos");

  const firstKey = await createKey(projectIds[0]);
  const secondKey = await createKey(projectIds[1]);
  const secondFirstKey = await createKey(projectIds[0]);

  assert((await runtimeGet(firstKey)).status !== 429);
  assert((await runtimeGet(firstKey)).status !== 429);
  const firstLimited = await runtimeGet(firstKey);
  assert(firstLimited.status === 429, `El proyecto A no recibió 429: ${firstLimited.status}`);
  assert((await firstLimited.json()).error.code === "RATE_LIMITED");
  assert(
    (await runtimeGet(secondFirstKey)).status === 429,
    "Otra clave del mismo proyecto no compartió el cupo."
  );
  pass("Proyecto A: tercera petición 429 y segunda clave comparte cupo");

  for (let index = 0; index < 5; index += 1) {
    assert((await runtimeGet(secondKey)).status !== 429, `El proyecto B fue limitado en ${index + 1}.`);
  }
  assert((await runtimeGet(secondKey)).status === 429, "El proyecto B no recibió 429 en la sexta petición.");
  pass("Proyecto B mantiene su cupo independiente de A");

  await page.reload({ waitUntil: "networkidle" });
  if ((await page.locator("#admin-token").count()) > 0) {
    await page.locator("#admin-token").fill(adminToken);
    await page.getByRole("button", { name: "Abrir panel" }).click();
  }
  await page.locator("#panel-projects").waitFor({ state: "visible" });
  await page.locator("#tab-projects").click();
  assert((await projectRow(page, slugs[0]).innerText()).includes("2"));
  assert((await projectRow(page, slugs[1]).innerText()).includes("5"));
  pass("El panel refleja los cupos efectivos tras recargar");

  await editLimit(page, slugs[0], null, null);
  const cleared = await api(`/api/v1/projects/${projectIds[0]}`);
  assert(cleared.rateLimitPermits === null && cleared.rateLimitWindowSeconds === null);
  assert(cleared.effectiveRateLimitPermits === 300 && cleared.effectiveRateLimitWindowSeconds === 60);
  pass("Vaciar los campos restaura el default global");

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
  assert(
    overflow.overflow <= 1,
    `Desbordamiento horizontal en móvil: ${JSON.stringify(overflow)}`
  );
  await page.screenshot({
    path: join(repositoryRoot, ".impeccable", "review", "rate-limits-mobile.png"),
    fullPage: true
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.screenshot({
    path: join(repositoryRoot, ".impeccable", "review", "rate-limits-desktop.png"),
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

async function editLimit(page, slug, permits, windowSeconds) {
  await projectRow(page, slug).getByRole("button", { name: "Cupo" }).click();
  const dialog = page.getByRole("dialog", { name: "Cupo de Runtime" });
  await dialog.waitFor({ state: "visible" });
  assert((await dialog.getAttribute("aria-labelledby")) !== null, "El diálogo no tiene aria-labelledby.");
  await dialog.locator("#project-rate-permits").fill(permits === null ? "" : String(permits));
  await dialog.locator("#project-rate-window").fill(windowSeconds === null ? "" : String(windowSeconds));
  await dialog.getByRole("button", { name: "Guardar cupo" }).click();
  await dialog.waitFor({ state: "hidden" });
}

function projectRow(page, slug) {
  return page.locator("#panel-projects tbody tr").filter({ hasText: slug });
}

async function createKey(projectId) {
  const created = await api(`/api/v1/projects/${projectId}/keys`, {
    method: "POST",
    body: JSON.stringify({ label: "rate-limit-e2e" })
  });
  assert(typeof created.key === "string" && created.key.length > 0, "La API no devolvió una clave pública.");
  return created.key;
}

function runtimeGet(apiKey) {
  return fetch(`${apiUrl}/rest/v1/contactos`, { headers: { apikey: apiKey } });
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
