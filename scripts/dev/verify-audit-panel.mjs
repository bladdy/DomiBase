#!/usr/bin/env node
/**
 * End-to-end verification of the administrative audit viewer from the real local stack.
 *
 * Requirements:
 * - PostgreSQL container `domibase-postgres` running with the EF migration applied.
 * - DomiBase API at DOMIBASE_API_URL (default http://127.0.0.1:5080). If it is not already
 *   listening, the script starts the built executable from backend/src/DomiBase.Api.
 * - Admin panel at ADMIN_PANEL_URL (default http://127.0.0.1:5190). If it is not already
 *   listening, the script starts `npm run dev` in frontend/admin.
 * - DOMIBASE_ADMIN_TOKEN in the environment or the repository `.env`.
 * - Playwright installed in frontend/admin and Chrome available as channel `chrome`.
 *
 * The script provisions one temporary project, emits real `api_key.create` audit rows,
 * exercises the panel filters and pagination, checks server-side redaction against the real
 * audit API, and verifies the provisioning step history. It also checks the 390×844 layout,
 * console cleanliness and removes the temporary project. No token, API key, prefix, password
 * or diagnostic content is printed.
 *
 * Usage: node scripts/dev/verify-audit-panel.mjs
 */

import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
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
const slug = `audit-e2e-${suffix}`;
const keyCount = 21;

let passed = 0;
let projectId = null;
let browser = null;
let apiProcess = null;
let panelProcess = null;

main().catch((error) => {
  console.error(`\n[FALLO] ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}).finally(async () => {
  if (projectId) {
    try {
      await deleteProject(projectId, slug);
      pass("Proyecto temporal y sus claves eliminados");
    } catch (error) {
      console.error(`[LIMPIEZA] No se pudo eliminar el proyecto temporal: ${message(error)}`);
      process.exitCode = 1;
    }
  }
  if (browser) {
    await browser.close();
  }
  stopProcess(panelProcess);
  stopProcess(apiProcess);
  console.log(`\nComprobaciones superadas: ${passed} · fallos: ${process.exitCode ? 1 : 0}`);
});

async function main() {
  assert(Boolean(adminToken), "Falta DOMIBASE_ADMIN_TOKEN en el entorno o en .env.");
  await ensureApi();
  await ensurePanel();

  const requireFromAdmin = createRequire(join(repositoryRoot, "frontend", "admin", "package.json"));
  const { chromium } = requireFromAdmin("playwright");

  console.log("Auditoría y provisioning · verificación E2E del panel");

  const created = await api("/api/v1/projects", {
    method: "POST",
    body: JSON.stringify({ slug, name: "Audit E2E" })
  });
  assert(created.status === "Ready", `El proyecto quedó en ${created.status}.`);
  projectId = created.id;
  pass("Proyecto temporal provisionado");

  const prefixes = [];
  for (let index = 0; index < keyCount; index += 1) {
    const key = await api(`/api/v1/projects/${projectId}/keys`, {
      method: "POST",
      body: JSON.stringify({ label: `audit-e2e-${index}` })
    });
    assert(typeof key.prefix === "string" && key.prefix.length > 0, "La API no devolvió el prefijo de clave.");
    prefixes.push(key.prefix);
  }
  pass(`${keyCount} claves públicas emitidas para generar auditoría real`);

  const audit = await api(
    `/api/v1/audit?projectId=${encodeURIComponent(projectId)}&action=api_key.create&limit=200`
  );
  assert(audit.total === keyCount, `La Control API devolvió ${audit.total} eventos de clave.`);
  assert(audit.items.every((item) => item.projectId === projectId), "Apareció un evento de otro proyecto.");
  const details = audit.items.map((item) => item.details ?? "").join("\n");
  assert(details.includes("[redacted]"), "La API no enmascaró el prefijo de la clave.");
  assert(prefixes.every((prefix) => !details.includes(prefix)), "La API filtró un prefijo de clave.");

  const actionAudit = await api(`/api/v1/audit?action=api_key.create&limit=200`);
  const filteredTotal = actionAudit.total;
  assert(filteredTotal >= keyCount, `El filtro global devolvió solo ${filteredTotal} eventos.`);
  pass("Auditoría filtrada por proyecto y redacción real aplicada por el servidor");

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

  await page.locator("#tab-audit").click();
  const panel = page.locator("#panel-audit");
  await panel.waitFor({ state: "visible" });
  assert((await page.locator("#tab-audit").getAttribute("aria-controls")) === "panel-audit");
  assert((await page.locator("h1").innerText()).includes("Auditoría"));
  await waitForAuditIdle(page);
  pass("Pestaña Auditoría accesible con su panel y encabezado");

  const auditTable = panel.locator(".audit-table").first();
  assert((await auditTable.locator("thead th").count()) === 6, "La tabla no tiene seis encabezados.");
  assert((await auditTable.locator("caption").count()) === 1, "La tabla no tiene caption accesible.");
  assert(
    (await auditTable.locator("tbody tr").first().locator("th[scope=row]").count()) === 1,
    "La fila no tiene encabezado de fila."
  );
  pass("Tabla de auditoría semántica con encabezados y caption");

  await panel.locator("#audit-action").fill("api_key.create");
  await panel.getByRole("button", { name: "Aplicar filtros" }).click();
  let status = await waitForStatus(page, `de ${filteredTotal} entradas`);
  const filteredActions = await auditTable.locator("tbody tr td:nth-child(2) code").allInnerTexts();
  assert(filteredActions.every((action) => action === "api_key.create"), "El filtro no se aplicó en la tabla.");
  pass("Filtro por acción aplicado desde el panel contra datos reales");

  await panel.locator("#audit-page-size").selectOption("20");
  status = await waitForStatus(page, `1–20 de ${filteredTotal} entradas`);
  assert((await auditTable.locator("tbody tr").count()) === 20, "La primera página no tiene 20 filas.");
  pass("Tamaño de página aplicado y primera página acotada");

  await panel.getByRole("button", { name: "Siguiente" }).click();
  const secondLast = Math.min(40, filteredTotal);
  status = await waitForStatus(page, `21–${secondLast} de ${filteredTotal} entradas`);
  const secondPageRows = Math.min(20, filteredTotal - 20);
  assert((await auditTable.locator("tbody tr").count()) === secondPageRows, "La segunda página no tiene las filas restantes.");
  await panel.getByRole("button", { name: "Anterior" }).click();
  await waitForStatus(page, `1–20 de ${filteredTotal} entradas`);
  pass("Paginación Siguiente/Anterior verificada con desplazamiento real");

  await panel.getByRole("button", { name: "Limpiar" }).click();
  await page.waitForFunction((expected) => {
    const status = document.querySelector("#panel-audit .audit-status");
    const input = document.querySelector("#audit-action");
    const text = status?.textContent ?? "";
    return input instanceof HTMLInputElement && input.value === ""
      && text.includes("de ") && !text.includes("Cargando")
      && !text.includes(`de ${expected} entradas`);
  }, filteredTotal, { timeout: 15000 });
  const visibleDetails = await auditTable.locator("tbody tr td:nth-child(6)").allInnerTexts();
  assert(visibleDetails.some((text) => text.includes("[redacted]")), "El panel no mostró la redacción.");
  assert(prefixes.every((prefix) => !visibleDetails.some((text) => text.includes(prefix))), "El panel filtró un prefijo.");
  pass("Redacción visible en la tabla y sin prefijos de clave en el DOM");

  const stepsTable = panel.locator(".audit-table").nth(1);
  await stepsTable.waitFor({ state: "visible" });
  const stepStatuses = await stepsTable.locator("tbody tr td:nth-child(2)").allInnerTexts();
  assert(stepStatuses.length >= 1, "El proyecto no tiene pasos de provisioning.");
  assert(stepStatuses.some((text) => text.includes("Succeeded")), "No apareció un paso Succeeded.");
  assert(
    (await stepsTable.locator("tbody tr").first().locator("th[scope=row]").count()) === 1,
    "La tabla de pasos no tiene encabezado de fila."
  );
  pass("Pasos de provisioning reales mostrados para el proyecto activo");

  await page.setViewportSize({ width: 390, height: 844 });
  await waitForAuditIdle(page);
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
          inTabs: Boolean(element.closest(".tabs"))
        };
      })
      .filter((item) => !item.inTabs)
      .filter((item) => item.right > window.innerWidth + 1 || item.scrollWidth > item.clientWidth + 1)
      .slice(0, 8);
    return {
      overflow: documentElement.scrollWidth - documentElement.clientWidth,
      innerWidth: window.innerWidth,
      offenders
    };
  });
  assert(overflow.overflow <= 1, `Desbordamiento horizontal en móvil: ${JSON.stringify(overflow)}`);
  const reviewDirectory = join(repositoryRoot, ".impeccable", "review");
  mkdirSync(reviewDirectory, { recursive: true });
  await page.screenshot({ path: join(reviewDirectory, "audit-mobile.png"), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await waitForAuditIdle(page);
  await page.screenshot({ path: join(reviewDirectory, "audit-desktop.png"), fullPage: true });
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

async function deleteProject(id, confirmSlug) {
  await api(`/api/v1/projects/${id}`, {
    method: "DELETE",
    headers: { "X-DomiBase-Confirm-Delete": "delete" },
    body: JSON.stringify({ confirmSlug })
  });
}

async function isReachable(url) {
  try {
    const response = await fetch(url);
    return response.ok;
  } catch {
    return false;
  }
}

async function ensureApi() {
  if (await isReachable(`${apiUrl}/health/ready`)) {
    pass(`API ya disponible en ${apiUrl}`);
    return;
  }

  const executable = join(
    repositoryRoot,
    "backend",
    "src",
    "DomiBase.Api",
    "bin",
    "Debug",
    "net10.0",
    "DomiBase.Api.exe"
  );
  assert(existsSync(executable), "La API no responde y no existe el ejecutable compilado. Ejecuta dotnet build.");
  apiProcess = spawn(executable, [], {
    cwd: dirname(executable),
    env: process.env,
    stdio: "ignore",
    windowsHide: true
  });
  await waitFor(async () => isReachable(`${apiUrl}/health/ready`), 40, 500);
  pass("API iniciada desde el ejecutable compilado");
}

async function ensurePanel() {
  if (await isReachable(panelUrl)) {
    pass(`Panel ya disponible en ${panelUrl}`);
    return;
  }

  const npm = process.platform === "win32" ? "npm.cmd" : "npm";
  panelProcess = spawn(npm, ["run", "dev"], {
    cwd: join(repositoryRoot, "frontend", "admin"),
    env: process.env,
    stdio: "ignore",
    windowsHide: true
  });
  await waitFor(async () => isReachable(panelUrl), 40, 500);
  pass("Panel iniciado con npm run dev");
}

function waitForAuditIdle(page) {
  return page.waitForFunction(() => {
    const status = document.querySelector("#panel-audit .audit-status");
    return Boolean(status?.textContent && !status.textContent.includes("Cargando"));
  }, undefined, { timeout: 15000 });
}

async function waitForStatus(page, expected, timeout = 15000) {
  const deadline = Date.now() + timeout;
  let last = "";
  while (Date.now() < deadline) {
    last = await page.locator("#panel-audit .audit-status").innerText().catch(() => "");
    if (last.includes(expected)) {
      return last;
    }
    await page.waitForTimeout(100);
  }
  throw new Error(`El estado esperado no apareció. Último estado: ${last}`);
}

async function waitFor(check, attempts, delay) {
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    if (await check()) {
      return;
    }
    await sleep(delay);
  }
  throw new Error("El servicio no respondió a tiempo.");
}

function stopProcess(child) {
  if (!child?.pid || child.exitCode !== null) {
    return;
  }
  if (process.platform === "win32") {
    spawnSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
  } else {
    child.kill("SIGTERM");
  }
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
