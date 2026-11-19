# Fase 1 — Database MVP (desarrollo local)

## Objetivo
Desde la API local, crear el proyecto `paneles-solares`, provisionar su base PostgreSQL, crear la tabla `contactos` y operar sobre ella desde una app Astro usando el SDK DomiBase. Durante todo el desarrollo **solo PostgreSQL se ejecuta en Docker**; API, SDK, tests y Astro se ejecutan directamente en Windows.

## Hallazgos de inspección (2026-11-16 y Fase 0)
- **Repositorio:** `C:\Proyectos\DomiBase`, rama `main` sincronizada con `origin/main`; cambios locales de documentación/preparación sin commit.
- **Herramientas:** .NET SDK `10.0.103`, Node.js `v24.13.1`, npm `11.8.0`, Docker CLI `29.8.2`, Docker Compose `v5.6.0`.
- **Docker Desktop:** motor Linux operativo (verificado en Fase 0).
- **Puerto local:** `5432` ocupado por otro stack (`postgres:15`); DomiBase usa `127.0.0.1:5433` (también se descartó `55432` por caer en un rango reservado de Windows/Hyper-V).
- **Stacks existentes:** no se modifican ni se detienen.
- **Homelab:** Ubuntu 26.04.1 LTS de 16 GB como objetivo futuro; contenedores/redes/volúmenes/puertos del homelab aún sin inventariar.
- **NexStack:** no hay implementación previa que migrar.
- **PostgreSQL:** imagen fija `postgres:18-alpine` validada (18.7 en ejecución). Volumen montado en `/var/lib/postgresql` (layout de PostgreSQL 18+).
- **Puerto:** `55432` se descartó al caer en el rango reservado `55375–55474` de Windows/Hyper-V; se usa `5433`.

## Prerrequisitos
- Docker Desktop con motor Linux operativo (solo PostgreSQL).
- .NET SDK 10 para ejecutar la API con `dotnet run`.
- Node.js/npm para SDK y Astro. En PowerShell usar `npm.cmd`/`npx.cmd` (o `cmd /c npm ...`) si los scripts `.ps1` están bloqueados por firma.
- Puerto `127.0.0.1:5433` libre y fuera de rangos reservados de Windows.
- Variables locales en `.env` (no versionado); `.env.example` solo placeholders.

## Fases

### Fase 0 — Inspección y documentación (completada)
- Estado git, estructura, herramientas y Docker.
- Informe de inspección y reconciliación documental.
- `docker-compose.yml` (solo PostgreSQL), `.env.example`, `.gitignore`, `.editorconfig`, `.gitattributes`.

### Fase 1 — PostgreSQL (solo Docker) — completada
- Contenedor `domibase-postgres` con `postgres:18-alpine` (18.7).
- Volumen persistente `domibase_postgres_data` montado en `/var/lib/postgresql` (layout PostgreSQL 18+).
- Healthcheck y `restart: unless-stopped`.
- Publicación únicamente en `127.0.0.1:5433`.
- Base `domibase_control`.
- Verificado: healthcheck `healthy`, conectividad TCP desde Windows, base de control creada y persistencia tras `stop`/`up` (sin comandos destructivos).

### Fase 2 — Backend base (Windows)
- `backend/DomiBase.sln` con proyectos: `DomiBase.Api`, `DomiBase.Domain`, `DomiBase.Application`, `DomiBase.Infrastructure`, `DomiBase.Provisioning` y tests.
- Configuración por variables de entorno y `appsettings`; carga opcional de `.env` en desarrollo.
- Logging estructurado, OpenAPI (dev), health checks.
- Conexiones separadas: metadatos (EF Core/Npgsql) y provisioning (Npgsql administrativo); Runtime en Fase 4.
- `GET /health`, `GET /health/ready`.
- Esqueletos de Control (`/api/v1`, autenticación administrativa) y Runtime (`/rest/v1/{table}`) con respuestas 501 honestas.
- Estado: **completada** (ver `phase-02.md`). Build sin warnings; 49 tests en verde; verificación manual contra PostgreSQL 18 en Docker.

### Fase 3 — Control Plane — completada
- Migración inicial de `domibase_control` (`projects`, `api_keys`, `provisioning_operations`, `audit_entries`) aplicada con `dotnet-ef` 10.0.15 local.
- Endpoints privados reales: `GET/POST /api/v1/projects`, `GET /api/v1/projects/{id}`, `POST /api/v1/projects/{id}/provision`, `GET/POST /api/v1/projects/{id}/keys` y `DELETE /api/v1/projects/{id}/keys/{keyId}`.
- API keys: `db_public_` + 32 bytes aleatorios, prefijo visible, HMAC-SHA-256 con `DOMIBASE_API_KEY_PEPPER`, valor completo solo al crear, revocación idempotente; `db_secret_` reservado.
- Provisioning idempotente y reintentable: `domibase_<slug>`, roles `_owner`/`_runtime`/`_anon` `NOLOGIN` y sin privilegios elevados, permisos mínimos, advisory lock y registro por paso.
- `CREATE DATABASE` fuera de transacción; conexión administrativa restringida al provisioning; auditoría persistida.
- Estado: **completada** (ver `phase-03.md`). Build sin warnings; 62 tests en verde; verificación manual contra PostgreSQL 18 en Docker.

### Fase 4 — Runtime Plane — completada
- Resolución de proyecto por API key pública (nunca desde `projectId` del cliente), verificación HMAC-SHA-256 del valor, revocación y estado `Ready`.
- Conexión con login técnico `_runtime` (`LOGIN NOINHERIT`) y `SET ROLE` transaccional al rol `_anon`; contraseña cifrada con AES-256-GCM.
- `GET/POST/PATCH/DELETE /rest/v1/{table}` implementados.
- Validación de tablas/columnas/operadores (allowlist desde el catálogo), SQL parametrizado y quoting seguro.
- `PATCH`/`DELETE` con filtros obligatorios.
- `SET LOCAL ROLE` + `set_config(..., true)` por transacción; probado que no se filtra el contexto entre conexiones pooled.
- En el cierre de esta fase: rate limiting por IP, límites de payload/filas, CORS configurable y errores normalizados. Estado vigente: P1.4 sustituye el rate limit por cuota configurable por proyecto con fallback por IP (`docs/implementation/p1-4-rate-limits.md`).
- Estado: **completada** (ver `phase-04.md`). Build sin warnings; **98 tests** en verde (73 unitarios, 19 integración, 6 seguridad).

### Fase 5 — SDK `@domibase/client` — completada
- `packages/client` con TypeScript estricto, build ESM, declaraciones y paquete `private: true`.
- `createClient`, query builder inmutable, `select`/`insert`/`update`/`delete`, filtros, `order`, `limit`, `range`, `abortSignal`, `single`/`maybeSingle`.
- `fetch` inyectable, `AbortSignal`, timeout, SSR-safe y errores `DomiBaseError` normalizados.
- Límites locales alineados con Runtime y validación de identificadores/valores antes de la red.
- **50 tests** en verde con `node --test` (wire format, operadores, mutaciones, filas únicas, transporte, timeout/abort y configuración).
- Estado: **completada** (ver `phase-05.md`). Pendiente la integración real en la Fase 6.

### Fase 6 — Integración Astro (Windows) — completada
- `examples/astro-example` consumiendo la API local con Astro 7 SSR y `@astrojs/node` standalone (puerto `4325`).
- `paneles-solares` y `aislamiento-demo` creados y provisionados; tabla `contactos` con RLS y políticas de demo.
- Insert/select/update/delete verificados, API key inválida y clave revocada rechazadas con 401, y aislamiento bidireccional entre proyectos.
- Nunca se expusieron credenciales administrativas en el navegador ni endpoints públicos de SQL.
- Estado: **completada** (ver `phase-06.md`). `astro check` sin errores, build standalone correcto y 5 formularios + 7 comprobaciones de diagnóstico en verde.

### Fase 7 — Validación final — completada
- `dotnet test backend/DomiBase.sln`: 98 en verde (73 unitarios, 19 integración, 6 seguridad).
- Tests SDK y build: 50 en verde con `node --test`.
- Integration/security tests contra PostgreSQL real en Docker, ya cubiertos en Fase 4.
- Persistencia tras reinicio (`docker compose stop`/`up`) verificada con datos reales; múltiples bases, roles, RLS y pooling cubiertos por los tests de integración/seguridad.
- Revisión de seguridad, documentación y estabilidad: barrido de secretos y validación post-reinicio con `scripts/dev/phase-07-validate.mjs`.
- Estado: **completada** (ver `phase-07.md`).

### Fase 8 — Preparación de imagen (sin construir/publicar) — completada
- Dockerfile multi-stage, base oficial fijada, usuario no root, configuración por variables, healthcheck y `.dockerignore`.
- Imagen futura `bladdy34/domibase-api:0.1.0`. **No ejecutar `docker build` ni publicar sin autorización.**
- Estado: **completada** (ver `phase-08.md`). Bases `10.0-noble` fijadas por digest; `docker buildx build --check` sin warnings; `dotnet publish` Release verificado localmente.

## Verificación de Fase 1
- `docker compose up -d` levanta solo `domibase-postgres`.
- Healthcheck en estado `healthy`.
- Conectividad desde Windows al puerto `5433`.
- `domibase_control` existe.
- Persistencia tras `docker compose stop` + `docker compose up -d`.
- No se ejecutó `docker compose down -v` ni se eliminaron volúmenes.

## Definition of Done
Build limpio, pruebas pasando, documentación actualizada, despliegue reproducible y ninguna credencial real en Git. La imagen final no se construye ni publica hasta cumplir los criterios de aceptación y obtener autorización.
