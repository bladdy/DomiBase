# Roadmap de implementación

## Contexto (2026-11-16)
- Repositorio `C:\Proyectos\DomiBase`, rama `main` sincronizada con `origin/main`.
- Desarrollo en Windows; destino futuro Ubuntu 26.04.1 LTS (16 GB) con Docker Engine y Portainer.
- Herramientas: .NET SDK `10.0.103`, Node.js `v24.13.1`, npm `11.8.0`, Docker `29.8.2`, Compose `v5.6.0`.
- Durante el desarrollo **solo PostgreSQL se ejecuta en Docker** (puerto local `5433`; `5432` ocupado por otro stack).
- Sin implementación previa de NexStack. Sin GoTrue, PostgREST, Supabase SDK ni código de Supabase.

> **Estado vigente (2026-11-18):** las fases y entregas históricas se conservan abajo, pero el panel se ha simplificado al alcance mínimo `Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK`. La fuente vigente es `docs/implementation/admin-minimal-scope.md`; la verificación ejecutable está en `docs/implementation/admin-minimal-e2e.md`. No se han construido ni publicado imágenes ni se han ejecutado commits/pushes.

## Fase 0 — Inspección y diseño (completada)
- [x] Inspeccionar repositorio y git status.
- [x] Inventariar herramientas y versiones.
- [x] Verificar Docker Desktop/motor Linux y Compose.
- [x] Verificar disponibilidad de puerto y elegir `5433` (fuera de rangos reservados).
- [x] PostgreSQL 18.7 en Docker (`domibase-postgres`) healthy, con volumen y persistencia verificada.
- [x] Backend base ASP.NET Core 10 con build limpio, health checks verdes y tests pasando (49 al cerrar Fase 2; 62 al cerrar Fase 3; 98 al cerrar Fase 4).
- [x] Informe de inspección y reconciliación documental.
- [x] Decidir contrato HTTP (`/api/v1`, `/rest/v1`, `/health`).
- [x] Preparar `.gitignore`, `.editorconfig`, `.gitattributes`, `.env.example`.

## Fase 1 — Database MVP
- [x] PostgreSQL 18 en Docker (`domibase-postgres`, solo desarrollo).
- [x] Solución .NET 10 (`backend/DomiBase.sln`) con proyectos por capa y tests.
- [x] Backend base: configuración, health checks, OpenAPI, autenticación administrativa y esqueletos Control/Runtime (Fase 2 completada).
- [x] Control Plane: proyectos, API keys y provisioning idempotente (Fase 3 completada; ver `phase-03.md`).
- [x] Runtime Plane: `/rest/v1/{table}` con RLS, allowlists y aislamiento (Fase 4 completada; ver `phase-04.md`).
- [x] SDK TypeScript `@domibase/client` (Fase 5 completada; ver `phase-05.md`; 50 tests del SDK en verde).
- [x] Ejemplo Astro (Fase 6 completada; ver `phase-06.md`): `examples/astro-example` con CRUD real, aislamiento entre `paneles-solares` y `aislamiento-demo`, clave inválida/revocada/ausente y build `standalone` verificados.
- [x] Pruebas de integración/seguridad contra PostgreSQL real y RLS (98 tests backend en verde tras la Fase 6).
- [x] Validación final (Fase 7 completada; ver `phase-07.md`): 98 tests backend + 50 del SDK, persistencia tras `docker compose stop`/`up` y aislamiento/seguridad post-reinicio con `scripts/dev/phase-07-validate.mjs`.
- [x] Preparación de Dockerfile (Fase 8 completada; ver `phase-08.md`): `infrastructure/docker/Dockerfile` multi-stage con bases fijadas por digest, usuario no root y healthcheck; `docker buildx build --check` sin warnings. **Sin construir ni publicar.**

**Fase 1 (Database MVP) completada.** Los 13 puntos del alcance y el criterio de aceptación están verificados.

## Fase 2 — Auth (completada; plan en `auth-plan.md`)
- [x] 2.1 Fundamentos criptográficos (`auth-01-foundations.md`): Argon2id, ECDSA P-256/ES256, refresh tokens; 120 tests en verde.
- [x] 2.2 Esquema y provisioning por proyecto (`auth-02-schema.md`): rol `authenticated`, schema `auth` con `users`/`refresh_tokens`, `NpgsqlAuthUserRepository`, claves de firma en control; 125 tests en verde.
- [x] 2.3 Endpoints `/auth/v1` (`auth-03-endpoints.md`): registro, login, logout, refresh; 138 tests en verde.
- [x] 2.4 Runtime autenticado (`auth-04-runtime.md`): Bearer validation, claims `sub` y RLS con rol `authenticated`; 138 tests en verde.
- [x] 2.5 SDK y ejemplo Astro (`auth-05-sdk.md`): `auth.*` en `@domibase/client`, login/cookies en Astro, tabla `notas` autenticada; 57 tests SDK + 138 tests backend en verde.

## Fase 3 — Realtime (completada)
- [x] Backend SignalR en `/realtime/v1` (`realtime-plan.md`, `realtime-01-backend.md`): hub `RealtimeHub`, `IRealtimeNotifier`, `SignalRRealtimeNotifier`, integración post-commit en `RuntimeDataService`.
- [x] Autorización de conexión por `apikey` y Bearer opcional; autorización de canal contra catálogo PostgreSQL con rol funcional.
- [x] Payload de evento mínimo: `{ table, operation, rowId, timestamp }`; clientes refetch vía Runtime para datos filtrados por RLS.
- [x] SDK realtime (`realtime-02-sdk.md`): `db.realtime.channel(table).subscribe(callback)` con `@microsoft/signalr`; 59 tests SDK en verde.
- [x] Ejemplo Astro (`realtime-03-astro.md`): página `/tiempo-real` con log de eventos.
- [x] Tests de integración backend SignalR: 3 tests en verde; total backend 141 tests en verde; total SDK 59 tests en verde.

## Fase 4 — Storage (completada; plan en `storage-plan.md`)
- [x] 4.1 Backend: entidades `StorageBucket`/`StorageObject`, migración EF Core, repositorio, `StorageService`, backend filesystem, endpoints Control (`/api/v1/projects/{id}/buckets`) y Runtime (`/storage/v1`).
- [x] 4.2 URLs firmadas: HMAC-SHA256 con `projectId` + bucket + path + expiración en la firma, validación en tiempo constante en el download y límite de validez de 7 días.
- [x] 4.3 Seguridad: upload streaming con límite y archivo temporal, protección contra path traversal, buckets públicos/privados, Bearer → `authenticated` para escribir/borrar, `anon` solo lectura en buckets públicos.
- [x] 4.4 SDK (`@domibase/client`): `db.storage.from(bucket).upload|download|list|remove|createSignedUrl`; transporte extendido con `sendBytes`/`sendRaw` sin romper la API JSON; 7 tests unitarios nuevos (66 total).
- [x] 4.5 Ejemplo Astro: página `/almacen` con subida, listado, borrado y URL firmada; navegación y script de demo actualizados (`demo-publico`).
- [x] 4.6 Tests: 11 tests de integración de Storage (CRUD, permisos anón/autenticado, signed URLs, firma manipulada, aislamiento entre proyectos, prefijos, path traversal). **152 tests backend en verde; 66 tests SDK en verde; build Astro verificado.**

## Fase 5 — Functions (completada; plan en `functions-plan.md`)
- [x] 5.1 Modelo de ejecución: módulos WebAssembly con ABI propia (`memory` + `invoke(i32,i32)->i64`), request `{ input, context }` y respuesta envuelta en `{ data }`.
- [x] 5.2 Aislamiento real: worker one-shot por invocación (`DomiBase.Functions.Worker`); el Runtime API nunca carga, compila ni interpreta código del usuario; sin WASI, imports, red ni filesystem en el guest.
- [x] 5.3 Límites: fuel y páginas de memoria en Wasmtime, epoch interruption, kill del proceso por timeout, input/output acotados, semáforo de concurrencia y errores estandarizados sin detalles internos.
- [x] 5.4 Control y Runtime: tabla `functions` con migración EF, endpoints `/api/v1/projects/{id}/functions` y `POST /functions/v1/{name}` con `apikey` y Bearer opcional.
- [x] 5.5 SDK y demo Astro: `db.functions.invoke(name, payload, { signal })` (70 tests SDK en verde) y página `/funciones` con contexto `userId` derivado de la sesión.
- [x] 5.6 Tests: 12 unitarios nuevos y 11 de integración (eco, contexto anónimo/autenticado, fuel, timeouts, imports rechazados, memoria, aislamiento entre proyectos, clave inválida, borrado y JSON inválido). **175 tests backend, 70 del SDK y build Astro en verde; validación final en `functions-plan.md`.**

## Fase 6 — Panel privado (completada; plan en `admin-panel-plan.md`)
- [x] 6.1 `frontend/admin` con React + Vite + TypeScript estricto, puerto fijo `5190` y proxy `/api` y `/admin` sin URL de API en el bundle.
- [x] 6.2 Acceso del propietario: token administrativo validado contra `GET /projects` y guardado solo en `sessionStorage`; cierre de sesión con confirmación.
- [x] 6.3 Proyectos: listado con estado/base/fallo, alta, selección y reintento de provisioning.
- [x] 6.4 Claves: emisión con valor único en memoria, listado sanitizado y revocación confirmada.
- [x] 6.5 Buckets y Funciones en solo lectura; tablas y políticas RLS quedan diferidas porque la Control API aún no las expone.
- [x] 6.6 Tests y verificación: 11 tests del cliente HTTP (instantánea al cerrar 6.6), typecheck, build con CSP, 20 comprobaciones de UI en Chrome headless (sesión, aislamiento de clave, accesibilidad y responsive) y detector de diseño sin hallazgos. Sistema visual documentado en `DESIGN.md`.
- [x] 6.7 Inspector de esquema en vivo (`schema-inspection.md`): pestaña Esquema con tablas, vistas y funciones del esquema `public`, tres endpoints GET en la Control Plane, catálogo PostgreSQL en transacción `READ ONLY`, auditoría por lectura y 182 tests backend + 14 del cliente en verde (instantánea al cerrar 6.7; el total vigente está en el estado general).

## Fase P0 — Evolución hacia BaaS (completada)
Objetivo: seguridad y ciclo de vida de proyectos antes de ampliar superficie funcional.

- [x] P0.2 Provisioning observable y compensable (`p0-2-provisioning.md`): pasos `Running`/terminales, `Attempt` y `CorrelationId`, compensación real, desprovisioning idempotente, guard de nombres y verificación de owner. **194 tests backend en verde** (116 unitarios, 69 integración, 9 seguridad). Sin migración EF: las columnas ya existían.
- [x] P0.1 Eliminación segura de proyectos (`p0-1-project-deletion.md`): `DELETE /api/v1/projects/{id}`, confirmación de cabecera + slug, estado `Deleting`, revocación de keys, limpieza idempotente y auditoría. **197 tests backend en verde** (116 unitarios, 71 integración, 10 seguridad). Sin migración EF.
- [x] P0.3 API Keys (`p0-3-api-keys.md`): emisión de claves `db_secret_` sin acceso al Runtime, rotación con solapamiento configurable, expiración server-side y auditoría. Migración `20261117225753_ApiKeyRotationAndExpiry`. **203 tests backend en verde** (116 unitarios, 76 integración, 11 seguridad).
- [x] P0.4 Secretos (`p0-4-secrets.md`): keyring de pepper (`DOMIBASE_API_KEY_PEPPERS`) y de AES (`DOMIBASE_SECRETS_KEYS`, formato `v2` con ID de clave), compatibilidad `v1`, verificación en tiempo constante y procedimiento de rotación/backup. **213 tests backend en verde** (125 unitarios, 77 integración, 11 seguridad). Sin migración EF.

**Fase P0 completada.** Las cuatro entregas (P0.2, P0.1, P0.3, P0.4) están implementadas, probadas y documentadas.

## Fase P1 — Panel de administración BaaS y operaciones

- [x] P1.1 Administración de esquema y políticas RLS (`p1-1-schema-admin.md`): DDL tipado y validado desde `/api/v1/projects/{id}/schema` para tablas, columnas, índices y políticas; ejecución privilegiada con `SET LOCAL ROLE` al owner, GRANT como alcance de comando, RLS activado por defecto, auditoría y errores sin SQL interno. **266 tests backend en verde** (169 unitarios, 83 integración, 14 seguridad). Sin migración EF: la configuración vive en cada base de proyecto y la auditoría reutiliza `audit_entries`.
- [x] P1.3 Migraciones versionadas por proyecto (`p1-3-migrations.md`): historial inmutable `domibase_migrations.applied` dentro de cada base de proyecto, planes tipados con checksum `v1.<SHA-256>`, orden creciente, idempotencia por versión+contenido, advisory lock por base, auditoría y errores sin SQL interno. Reutiliza el validador y el DDL de P1.1 vía `SchemaRequestParser`/`ProjectSchemaResolver`. **285 tests backend en verde** (180 unitarios, 88 integración, 17 seguridad). Sin migración EF.
- [x] P1.2 SQL Editor administrativo (`p1-2-sql-editor.md`): consola de solo lectura en el panel con validador léxico, transacción `READ ONLY`, `SET LOCAL ROLE`, timeout, row cap, `to_jsonb` y auditoría con fingerprint sin SQL. Endpoint `POST /api/v1/projects/{id}/sql/query`, nunca en `/rest/v1`. **334 tests backend en verde** (212 unitarios, 102 integración, 20 seguridad) y **18 tests del panel**. Sin migración EF.
- [x] P1.5 Catálogo de extensiones PostgreSQL (`p1-5-extensions.md`): allowlist de diez extensiones `trusted`, catálogo en vivo desde `pg_available_extensions`/`pg_extension` en transacción `READ ONLY`, DDL tipado e idempotente con `SET LOCAL ROLE` al owner, auditoría sin SQL y pestaña **Extensiones** en el panel. Endpoints `GET/POST/DELETE /api/v1/projects/{id}/extensions`, nunca en `/rest/v1`. **367 tests backend en verde** (231 unitarios, 113 integración, 23 seguridad) y **22 tests del panel**. Sin migración EF.
- [x] P1.4 Rate limit por proyecto (`p1-4-rate-limits.md`): cuotas editables desde `PATCH /api/v1/projects/{id}/rate-limit` (override `NULL` hereda 300/60 s; rangos 1–10000 y 1–3600), partición en memoria `project:{id}:{permits}:{window}` compartida por todas las claves del proyecto y fallback `ip:{dirección}` con defaults para claves inválidas. Resolución única del `apikey` vía middleware; política `"runtime"` en REST, Storage, Functions, Auth y handshake de Realtime (no frames WebSocket), con 429 `RATE_LIMITED` canónico y auditoría `project.rate_limit.updated`. Migración EF `20261118032531_AddProjectRateLimits` aplicada solo a `domibase_control`. Columna **Cupo** y diálogo nativo en el panel. **383 tests backend en verde** (244 unitarios, 116 integración, 23 seguridad) y **35 tests del panel**; E2E real **14/14** (`scripts/dev/verify-rate-limits-panel.mjs`). Sin Redis ni homelab.
- [x] Panel de migraciones versionadas (extensión de la Fase 6 sobre P1.3): pestaña **Migraciones** con historial, constructor tipado de ocho operaciones, validación local contra las allowlists del servidor, vista previa JSON y confirmación con `<dialog>` nativo. Consume `GET/POST /api/v1/projects/{id}/migrations`; nunca envía SQL ni expone administración en `/rest/v1`. `add_column`/`alter_column` quedan fuera del constructor por la colisión del campo `type` en el parser. **367 tests backend y 34 tests del panel en verde**; build de producción correcto. Verificación E2E real con Chrome headless (`scripts/dev/verify-migrations-panel.mjs`): **15/15 comprobaciones**, incluida la corrección responsive del planner en 390×844. Sin migración EF.

- [x] P1.6 Backups y restauración lógica por proyecto (`p1-6-backups.md`): `pg_dump --format=custom` y `pg_restore --clean --if-exists --single-transaction --exit-on-error --no-owner` por base de proyecto, con contraseña solo en `PGPASSWORD`, rol owner vía `PGOPTIONS`, lock advisory compartido con provisioning, SHA-256 verificado antes de restaurar, confirmación `X-DomiBase-Confirm: <slug>` y estados `Provisioning`→`Ready`/`Failed`. Cinco endpoints Control bajo `/api/v1/projects/{id}/backups`, nunca en `/rest/v1`. Migración EF `20261118040353_AddProjectBackups` aplicada solo a `domibase_control`. Pestaña **Backups** con tabla semántica y diálogo nativo. **427 tests backend en verde** (272 unitarios, 120 integración, 35 seguridad) y **42 tests del panel**; E2E real **18/18** (`scripts/dev/verify-backups-panel.mjs`), con recuperación de datos verificada. Sin Docker de API ni homelab.
- [x] P1.7 Visor de auditoría y logs de provisioning (`p1-7-audit-viewer.md`): lectura acotada desde la Control Plane con `GET /api/v1/audit` y `GET /api/v1/projects/{id}/provisioning-operations`, nunca bajo `/rest/v1`. Migración EF `20261118045153_AddAuditProjectId` (`project_id` en `audit_entries`, backfill por regex UUID e índice) aplicada solo a `domibase_control`. `AuditQuery` valida parámetros, charset, fechas, `limit` 1–200 y `offset` 0–1.000.000; `AuditRedactor` enmascara secretos al almacenar y al leer. Pestaña **Auditoría** con filtros, paginación, tablas semánticas adaptadas por `@container` y pasos del proyecto activo. **458 tests backend en verde** (288 unitarios, 129 integración, 41 seguridad) y **45 tests del panel**; E2E real **16/16** (`scripts/dev/verify-audit-panel.mjs`) con redacción comprobada, 390×844 sin overflow y consola limpia. Sin Docker de API ni homelab.

## Fase P2 — Exploración de datos y políticas desde el panel

- [x] P2.1 Explorador de filas y panel de políticas RLS (`p2-1-data-explorer-rls.md`): nueva ruta Control `GET /api/v1/projects/{id}/data/tables/{table}/rows` que reutiliza el camino del Runtime y ejecuta la lectura como `anon`, con RLS, GRANT y contexto por proyecto reales; `select`, `f`, `order`, `limit` y `offset` validados contra allowlists, auditoría `data.preview` y nunca rol/base/SQL/clave desde el cliente. El panel añade las divisiones **Datos** y **Políticas RLS**: explorador con filtros acotados y editor de políticas con comandos, roles y tres expresiones allowlisted por el servidor. **466 tests backend en verde** (288 unitarios, 134 integración, 44 seguridad) y **49 tests del panel**; E2E real con Chrome headless y limpieza de tabla/política temporal, sin overflow a 1440/900/390 px. Sin migración EF, Docker de API ni homelab.

- [x] P2.2 Rediseño visual del panel (`p2-2-admin-redesign.md`): sustitución del antiguo mundo editorial por una consola tipo Studio con raíl oscuro persistente, superficies claras, tablas densas, acento verde, soporte de tema oscuro y adaptación a 1440/900/390 px. Cambio exclusivamente visual en `frontend/admin`: sin cambios en contratos, endpoints, tokens, RLS ni scripts E2E de seguridad. **49/49 tests del panel** y build de producción correctos.

Las entregas pendientes se planificarán individualmente; no se marcan como completadas por existir documentación.

## Condición de avance
Una fase no se marca terminada sin build, pruebas relevantes, documentación y criterios de aceptación cumplidos. La imagen de la API no se construye ni publica antes de completar la validación local y obtener autorización.
