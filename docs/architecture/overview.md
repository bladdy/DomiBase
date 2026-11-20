# Arquitectura general de DomiBase

## Objetivo
Construir una plataforma backend privada, administrada por un único propietario, con APIs y SDK propios. La primera entrega prioriza un flujo completo y seguro: crear proyecto, provisionar base PostgreSQL, exponer CRUD limitado y consumirlo desde Astro.

## Decisiones arquitectónicas confirmadas
- **Producto:** DomiBase (nombre único; sin migración NexStack pendiente).
- **Backend:** ASP.NET Core 10 / C#, **un único host** (modular monolith) con dos áreas lógicas estrictamente separadas.
- **SDK:** TypeScript propio, paquete provisional `@domibase/client` (sin `@supabase/supabase-js`, sin GoTrue, sin PostgREST, sin código de Supabase).
- **Panel privado:** React + Vite + TypeScript en `frontend/admin`; superficie mínima vigente: login, Proyectos, Resumen, Table Editor y Conexión. Las capacidades administrativas anteriores quedan fuera de la interfaz y del SDK soportado, aunque su código backend/shared se conserva.
- **Base de datos:** PostgreSQL 18 con imagen fija `postgres:18-alpine`; un servidor compartido y una base independiente por proyecto.
- **Desarrollo local:** Windows. Solo PostgreSQL se ejecuta en Docker; API, SDK, tests y ejemplo Astro se ejecutan directamente con `dotnet run`, `npm` y `node`.
- **Despliegue objetivo:** Ubuntu 26.04.1 LTS, Docker Engine y Portainer; proxy externo Nginx Proxy Manager existente.
- **Imagen futura:** `bladdy34/domibase-api` (Dockerfile preparado, sin construir ni publicar hasta superar pruebas y obtener autorización).

## Planes separados (mismo proceso)

### Control Plane
Administra proyectos, metadatos, credenciales, esquemas, políticas y auditoría. Solo el propietario puede acceder. Endpoints privados bajo `/api/v1`. Opera sobre `domibase_control`, que **nunca** es accesible desde el SDK público ni desde claves públicas del Runtime. El inspector de esquema consulta `pg_catalog` de la base de cada proyecto con transacción `READ ONLY`, esquema fijo `public`, sin SQL ni identificadores del cliente y con auditoría por lectura (`docs/implementation/schema-inspection.md`). Desde P1.1 también administra tablas, columnas, índices y políticas RLS mediante operaciones DDL tipadas y validadas, ejecutadas con la conexión privilegiada de provisioning y `SET LOCAL ROLE` al owner del proyecto (`docs/implementation/p1-1-schema-admin.md`). Desde P1.3 mantiene un historial de migraciones versionado e inmutable por proyecto en el esquema privado `domibase_migrations`, que no es visible para el inspector (`public`) ni para el Runtime (`docs/implementation/p1-3-migrations.md`).

### Runtime Plane
Atiende solicitudes de las aplicaciones. Identifica el proyecto mediante una API key pública, verifica su estado, aplica límites y ejecuta consultas bajo roles PostgreSQL restringidos con **RLS y GRANT** obligatorios. Endpoints públicos de datos bajo `/rest/v1`. No expone endpoints de administración.

Implementado en Fase 4: resolución server-side por hash de clave, conexión con el login `_runtime` del proyecto, transacción corta con `SET ROLE _anon` y claims (`request.jwt.claims`, `domibase.project_id`), allowlists derivadas del catálogo (tabla/columnas/privilegios), SQL parametrizado, `PATCH`/`DELETE` acotados por filtro, rate limiting por proyecto desde P1.4 (fallback por IP para claves inválidas), límites de body/filas y errores normalizados sin SQL ni secretos.

Ambas áreas comparten el mismo proceso, pero separan responsabilidades, servicios, endpoints, autenticación y conexiones. El diseño permite una futura extracción a deployables independientes sin rediseñar contratos.

## Contrato HTTP inicial
- **Sistema:** `GET /health`, `GET /health/ready`.
- **Control Plane:** `/api/v1/projects` (GET/POST), `/api/v1/projects/{id}` (GET/DELETE con confirmación), `/api/v1/projects/{id}/provision` (POST, reintento), `/api/v1/projects/{id}/keys` (GET/POST), `/api/v1/projects/{id}/keys/{keyId}` (DELETE), `/api/v1/projects/{id}/keys/{keyId}/rotate` (POST), `/api/v1/projects/{id}/schema/{tables|views|functions}` (GET) y administración de esquema bajo `/api/v1/projects/{id}/schema/tables`: POST/DELETE de tablas, POST/PATCH/DELETE de columnas e índices y GET/POST/PATCH/DELETE de políticas (P1.1), `/api/v1/projects/{id}/migrations` (GET/POST, P1.3) y `/api/v1/projects/{id}/sql/query` (POST de solo lectura, P1.2).
- **Runtime Plane:** `GET|POST|PATCH|DELETE /rest/v1/{table}` con filtros, orden y límites validados server-side.

## Despliegue inicial
Modular monolith en un host ASP.NET Core. PostgreSQL compartido. Panel React estático con superficie mínima. Realtime y Storage no forman parte de la interfaz ni del SDK soportado. Sin microservicios, Kubernetes ni Redis al inicio.

## Aislamiento
Una base de datos PostgreSQL por proyecto. El aislamiento de red no reemplaza el aislamiento de credenciales y permisos. Runtime no utiliza roles propietarios ni superuser y no puede omitir RLS.

## Provisioning
Flujo idempotente y reintentable con estados `Provisioning`, `Ready`, `Failed` y `Deleting`. Crear base de datos fuera de una transacción PostgreSQL ordinaria. Cada paso debe poder verificarse, reintentarse y compensarse sin duplicar recursos. La conexión administrativa queda restringida al provisioning.

Implementado en Fase 3: advisory lock por base; pasos `roles`, `database` y `database_permissions`; creación de roles restringidos y de la base con owner propio; permisos mínimos; registro por paso en `provisioning_operations`; reintento mediante `POST /api/v1/projects/{id}/provision`. El provisioning es síncrono dentro de la petición y no se cancela si el cliente se desconecta.

## Roles PostgreSQL por proyecto
- `domibase_<slug>_owner`: propietario/migraciones; no usado por Runtime.
- `domibase_<slug>_runtime`: login técnico limitado del Runtime (`LOGIN NOINHERIT`, miembro de `_anon`).
- `domibase_<slug>_anon`: rol funcional para solicitudes anónimas (`NOLOGIN`).
- `authenticated`: reservado para fase posterior.
- En Fase 4 el login `_runtime` recibe una contraseña aleatoria cifrada con AES-256-GCM en `domibase_control`; el Runtime la descifra solo en memoria y nunca usa credenciales administrativas. Desde P0.4 el cifrado usa el keyring `DOMIBASE_SECRETS_KEYS` (formato `v2` con ID de clave) y `DOMIBASE_SECRETS_KEY` se conserva solo para descifrar valores `v1` anteriores.
- Tablas expuestas: solo `public`, `relkind IN ('r','p')` y RLS habilitado. Los `GRANT` de tabla/columna determinan las operaciones permitidas. Las tablas creadas por la Control API nacen con `ENABLE ROW LEVEL SECURITY`: sin GRANT ni política no hay acceso, incluso con una API key válida.

## Contexto de inspección (2026-11-16)
- Repositorio `C:\Proyectos\DomiBase`, rama `main` sincronizada con `origin/main`, working tree inicialmente limpio.
- Desarrollo actual en Windows; entorno objetivo homelab Ubuntu 26.04.1 LTS de 16 GB.
- Herramientas locales: .NET SDK `10.0.103`, Node.js `v24.13.1`, npm `11.8.0`, Docker CLI `29.8.2` con Docker Desktop/motor Linux operativo.
- Contenedores, redes, volúmenes y puertos del homelab todavía sin inventariar (no se modifica el homelab en desarrollo).
- Puerto local `5432` ocupado por otro stack; DomiBase utiliza `127.0.0.1:5433` para PostgreSQL en desarrollo.
- No hay implementación previa de NexStack que migrar.

## Decisiones pendientes / validaciones requeridas
- Inventario del homelab (contenedores, redes, puertos, volúmenes, servicios) antes de desplegar.
- Puertos y endpoints definitivos del despliegue (no asumir puertos libres; PostgreSQL no público).
- Estrategia operativa de secretos en el homelab: el keyring de pepper y el keyring AES (`p0-4-secrets.md`) ya soportan rotación sin interrupción; queda por decidir el gestor de secretos y la ubicación del backup de las claves antes del despliegue.
- Scope npm `@domibase` creado y `@domibase/client@0.1.0` publicado con tag `latest`.
- Compatibilidad cross-platform de tooling (Windows dev / Ubuntu target); en PowerShell usar `npm.cmd`/`npx.cmd` si los scripts `.ps1` están bloqueados por firma.

## Estado (2026-11-18)
- **Estado vigente del alcance mínimo:** flujo `Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK` verificado de extremo a extremo. Backend **491 tests** (311 unitarios, 134 de integración y 46 de seguridad), SDK **70**, panel **57** y Astro sin errores de `astro check`. E2E 14/14 y 15/15 tras reiniciar PostgreSQL. Ver `docs/implementation/admin-minimal-scope.md` y `docs/implementation/admin-minimal-e2e.md`.
- PostgreSQL 18.7 operativo en Docker (`domibase-postgres`, `postgres:18-alpine`), healthy en `127.0.0.1:5433`.
- Base `domibase_control` creada; persistencia verificada tras `stop`/`up`.
- Volumen `domibase_postgres_data` montado en `/var/lib/postgresql` (layout PostgreSQL 18+).
- Backend ASP.NET Core 10 (`backend/DomiBase.sln`) operativo en Windows: build limpio, health checks verdes, OpenAPI y batería vigente de **491 tests** (311 unitarios, 134 de integración y 46 de seguridad). La cifra 458/288/129/41 era la instantánea de P1.7.
- Fase 3 completada: migración EF Core aplicada, Control Plane real (proyectos, API keys con HMAC-SHA-256 + pepper, provisioning idempotente de base/roles) y auditoría persistida.
- Fase 4 completada: Runtime Plane real (`/rest/v1/{table}`) con resolución por API key, login `_runtime` por proyecto, cifrado AES-256-GCM de contraseñas, allowlists, RLS, aislamiento de claims/pooling, rate limiting y límites de payload.
- Fase 5 completada: SDK TypeScript propio `@domibase/client` en `packages/client` (ESM, TypeScript estricto, query builder inmutable, transporte HTTP, `fetch` inyectable, timeout, `AbortSignal`, `single`/`maybeSingle` y errores normalizados); 70 tests del SDK en verde con `node --test` y exports activos recortados a la API soportada (la cifra 50 era la instantánea de la Fase 5).
- Fase 6 completada: ejemplo `examples/astro-example` (Astro 7 SSR + `@astrojs/node` standalone, puerto 4325) consumiendo el SDK contra el Runtime real; CRUD verificado, aislamiento entre `paneles-solares` y `aislamiento-demo`, rechazo de clave inválida/revocada/ausente y CSP/cabeceras en producción. Ver `docs/implementation/phase-06.md`.
- Fase 7 completada: persistencia tras `docker compose stop`/`up` comprobada con datos reales; aislamiento, bloqueo de `domibase_control` y claves inválida/revocada/ausente verificados post-reinicio con `scripts/dev/phase-07-validate.mjs`; 98 tests backend y 50 del SDK en verde (instantánea de la Fase 7; el total vigente está en el estado general). Ver `docs/implementation/phase-07.md`.
- Fase 8 completada: `infrastructure/docker/Dockerfile` multi-stage preparado (bases `10.0-noble` fijadas por digest, usuario no root, healthcheck) con `docker buildx build --check` sin warnings y `dotnet publish` Release verificado; **sin construir ni publicar**. Ver `docs/implementation/phase-08.md`.
- Panel privado (`frontend/admin`) reducido a la superficie mínima acordada: login con sesión `dbsess_`, lista de proyectos con crear/editar cupo/eliminar por slug, y dentro de proyecto **Resumen**, **Table Editor** (tablas, columnas, tipos, PK/FK/NOT NULL/UNIQUE/DEFAULT/IDENTITY, registros y RLS `anon_select`/`anon_insert`) y **Conexión** (URL, clave pública, metadatos PostgreSQL no secretos, JSON y ejemplo del SDK). Auth de usuarios finales, Storage, Realtime, Functions, SQL Editor, Migraciones, Extensiones, Backups, Auditoría, organizaciones y facturación no aparecen en navegación ni en el SDK soportado; su código backend/shared se conserva como registro histórico. 491 tests backend y 57 del panel en verde. Ver `docs/implementation/admin-minimal-scope.md` y `docs/implementation/admin-minimal-e2e.md`.
- P0.2 completado: provisioning con pasos trazados, compensación real, desprovisioning idempotente y guard de nombres. Ver `docs/implementation/p0-2-provisioning.md`.
- P0.1 completado: `DELETE /api/v1/projects/{id}` con confirmación doble, revocación de claves, limpieza idempotente y auditoría. Ver `docs/implementation/p0-1-project-deletion.md`.
- P0.3 completado: claves secretas emisibles pero rechazadas por el Runtime, rotación con solapamiento y expiración server-side. Ver `docs/implementation/p0-3-api-keys.md`.
- P0.4 completado: keyrings rotables para el pepper de API keys y para el cifrado AES de credenciales, con compatibilidad `v1` y procedimiento de backup. Ver `docs/implementation/p0-4-secrets.md`.
- P1.1 completado: DDL administrativo y políticas RLS desde la Control API, con validación estricta, privilegios de owner por transacción, GRANT como alcance de comando, auditoría y errores sin SQL interno. Ver `docs/implementation/p1-1-schema-admin.md`.
- P1.3 completado: migraciones versionadas por proyecto con historial privado `domibase_migrations.applied`, checksum `v1.<SHA-256>`, orden creciente, idempotencia, advisory lock por base y reutilización del DDL validado de P1.1. Ver `docs/implementation/p1-3-migrations.md`.
- P1.2 completado: SQL Editor administrativo de solo lectura con validador léxico, transacción `READ ONLY`, `SET LOCAL ROLE`, timeout, row cap, serialización `to_jsonb` y auditoría con fingerprint sin SQL. Ver `docs/implementation/p1-2-sql-editor.md`.
- P1.4 completado: cuota de Runtime por proyecto con defaults globales, override auditado desde la Control API, partición compartida por las claves del proyecto, fallback por IP y cobertura en REST, Storage, Functions, Auth y handshake de Realtime. Ver `docs/implementation/p1-4-rate-limits.md`.
- P1.5 completado: catálogo allowlisted de extensiones `trusted`, instalación y desinstalación tipadas desde la Control API y el panel. Ver `docs/implementation/p1-5-extensions.md`.
- P1.6 completado: backups lógicos `pg_dump`/`pg_restore` por proyecto, hash verificado, restauración transaccional confirmada y pestaña **Backups** en el panel. Ver `docs/implementation/p1-6-backups.md`.
- P1.7 completado: visor de auditoría y pasos de provisioning solo en la Control Plane, con redacción de secretos, filtros validados y pestaña **Auditoría** en el panel. Ver `docs/implementation/p1-7-audit-viewer.md`.
- P2.1 completado: explorador de filas con RLS real y editor de políticas RLS allowlisted en el panel. La vista previa reutiliza el camino del Runtime, se ejecuta como `anon` y queda auditada como `data.preview`; el cliente nunca envía rol, base, SQL ni clave. Ver `docs/implementation/p2-1-data-explorer-rls.md`.
- **Fases 1–6, serie P0, entregas P1.1–P1.7 y P2.1 completadas**, con el panel posteriormente simplificado al alcance mínimo. Estado vigente: 491 tests backend, 70 del SDK y 57 del panel en verde; E2E del flujo mínimo y de Astro verificados. Pendiente: despliegue futuro en el homelab (requiere inventario y autorización). No se han construido ni publicado imágenes ni se han ejecutado commits/pushes.
