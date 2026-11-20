# Informe de inspección inicial — DomiBase

**Fecha:** 2026-11-16  
**Alcance:** inspección inicial exclusivamente de lectura (sin instalación de paquetes, sin creación de contenedores, sin modificación de servicios existentes ni implementación).

> **Actualización 2026-11-19:** el estado del paquete npm quedó resuelto con la publicación de `@domibase/client@0.1.0` con tag `latest`; las referencias históricas a "no verificado" se han ajustado en los puntos correspondientes.

**Revisado:** `AGENTS.md`, `README.md`, `docs/architecture/overview.md`, `docs/architecture/security.md`, `docs/implementation/phase-01.md`, `docs/implementation/roadmap.md`, `docs/sdk/api-contract.md`, estructura completa del repositorio, estado/historial Git y herramientas locales.

---

## 1. Estado actual del repositorio

- **Ubicación/raíz:** `C:\Proyectos\DomiBase` (workspace). Git: sí.
- **Rama y remoto:** `main` rastreando `origin/main` (sin divergencias reportadas). Remoto: `https://github.com/bladdy/DomiBase.git` (fetch/push).
- **Working tree:** limpio según `git status --porcelain` (sin cambios staged/unstaged/untracked visibles al momento de la inspección).
- **Historial:** 1 commit — `d9e26ce` (`first commit`).
- **Estructura completa (archivos/directorios relevantes observados):**
  - Raíz: `AGENTS.md` (9283 B), `README.md` (1068 B), `.git/`
  - `docs/architecture/overview.md` (1540 B)
  - `docs/architecture/security.md` (1747 B)
  - `docs/implementation/phase-01.md` (2232 B)
  - `docs/implementation/roadmap.md` (1541 B)
  - `docs/sdk/api-contract.md` (1735 B)
- **Ausencias notables:** no hay `.gitignore`, `.editorconfig`, `.env*`, solución `.sln`, proyectos .NET, `package.json`, código fuente (`backend/`, `frontend/`, `packages/`, `examples/`, `infrastructure/`) ni configuración Docker/CI. No se observaron archivos ocultos adicionales fuera de `.git`.
- **Búsqueda NexStack:** solo aparecen referencias textuales en `AGENTS.md:214` y `docs/implementation/roadmap.md:7` como instrucción de “actualizar si existen”; **no se detectaron nombres/implementaciones NexStack que migrar** en el contenido actual.

---

## 2. Infraestructura disponible (entorno local de inspección)

| Herramienta | Resultado |
|---|---|
| .NET SDK | `10.0.103` (`C:\Program Files\dotnet\dotnet.exe`) |
| Docker CLI | `29.8.2, build 7fc2dff` (`C:\Program Files\Docker\Docker\resources\bin\docker.exe`) |
| Docker daemon/engine | **No accesible**: `docker ps/network/volume` fallaron contra `npipe:////./pipe/dockerDesktopLinuxEngine` (“daemon is running?” / pipe no encontrado). No fue posible inventariar contenedores, redes, puertos ni volúmenes Docker desde esta sesión. |
| Node.js | `v24.13.1` (`C:\nvm4w\nodejs\node.exe`) |
| npm | `11.8.0` vía `npm.cmd` (`C:\nvm4w\nodejs\npm.cmd`). `npm`/`npx` scripts PowerShell (`.ps1`) bloqueados por política de firma en esta sesión; `cmd /c npm -v` funciona. |
| Git | Disponible (`C:\Program Files\Git\cmd\git.exe`) |
| Clientes PostgreSQL (`psql`, `pg_dump`, `createdb`) | No detectados en `PATH` |
| Otros | `python.exe` (Python 3.13), `py.exe`, `winget.exe`, `choco.exe` presentes |

- **Plataforma de inspección:** Windows (`win32`). `AGENTS.md` define host objetivo Ubuntu homelab (16 GB RAM) con Docker Compose/Portainer y Nginx Proxy Manager existente; **la inspección actual no confirma acceso SSH ni estado real del homelab**.
- **Homelab (contenedores/redes/puertos/volúmenes):** sin acceso verificable en esta ejecución (daemon Docker local inactivo/no expuesto; no se proporcionaron credenciales ni contexto de acceso remoto). Requiere inspección directa en el host homelab antes de cualquier despliegue.

---

## 3. Conflictos detectados y riesgos

1. **Docker no operativo localmente:** no se puede validar Compose, redes, puertos publicados ni volúmenes existentes; impide levantar PostgreSQL de desarrollo/integración hasta habilitar el engine.
2. **Inventario de homelab desconocido:** alto riesgo de colisión con stacks existentes, puertos, redes Docker, volúmenes y NPM externo si se despliega sin inventario previo (“no asumir puertos libres / no tocar stacks existentes”).
3. **Desalineación de entorno documentado vs real:** docs asumen Ubuntu homelab/Portainer; trabajo actual en Windows. Comandos, paths, scripts y bind mounts deberán ser compatibles/cross-platform o explícitamente separados por entorno.
4. **npm en PowerShell restringido:** la política de ejecución/firma impide `npm.ps1`/`npx.ps1`; scripts de automatización deben preferir `npm.cmd`/`npx.cmd` o `cmd`.
5. **Clientes PostgreSQL ausentes en `PATH`:** dificulta diagnósticos manuales fuera de contenedor (no bloqueante si se opera vía contenedor/driver).
6. **Ausencia de `.gitignore`:** riesgo de introducir accidentalmente `bin/`, `obj/`, `node_modules/`, `.env`, artefactos y secretos al empezar la implementación.
7. **Paquete npm `@domibase/client`:** disponibilidad verificada: scope creado y `@domibase/client@0.1.0` publicado con tag `latest`.
8. **PostgreSQL 18:** decisión sujeta a validar imagen/compatibilidad del entorno; sin Docker operativo ni inventario de imágenes, no confirmado.
9. **NexStack:** sin conflictos de código/nombres encontrados en el estado actual.

---

## 4. Arquitectura confirmada o ajustes necesarios

### Confirmada (alineada con `AGENTS.md` y `docs/`)

- Producto propio DomiBase; sin `@supabase/supabase-js`, GoTrue, PostgREST ni capa de configuración Supabase.
- Separación **Control Plane** (admin, metadatos, provisioning, claves, auditoría) y **Runtime Plane** (resolución por API key, CRUD, políticas; sin endpoints admin).
- **Modular monolith** inicial: Control API y Runtime API (ASP.NET Core 10/C#), PostgreSQL compartido, **una base independiente por proyecto** (no un contenedor por proyecto). Sin microservicios/Kubernetes/Redis al inicio.
- Base `domibase_control` inaccesible desde claves públicas Runtime. Provisioning idempotente/reintentable/compensable; `CREATE DATABASE` fuera de transacción ordinaria.
- Seguridad PostgreSQL: roles/owner separados, Runtime sin superuser/owner/`BYPASSRLS`, GRANT mínimos, RLS en tablas expuestas, `SET LOCAL ROLE` + `set_config(..., true)` por transacción, sin filtrado de contexto en pool. API keys `db_public_` (hash, mostrar solo al crear/rotar), `db_secret_` reservado.
- SDK TypeScript propio (`@domibase/client`, provisional), contrato `{ data, error, count? }`, sin Auth/Realtime/Storage mock en fase 1. Panel React+Vite+TS. Infra objetivo Docker Compose/Portainer + Nginx Proxy Manager externo.

### Ajustes necesarios (según infraestructura real observada)

- **Validar host objetivo antes de implementar/desplegar:** confirmar acceso al homelab Ubuntu y ejecutar allí el inventario de contenedores/redes/puertos/volúmenes/servicios (incl. NPM/Portainer) y capacidad (16 GB) antes de fijar puertos y recursos.
- **Estrategia de ejecución local (Windows, Docker daemon actualmente caído):** habilitar Docker Desktop/engine o definir entorno Linux/WSL equivalente para desarrollo y pruebas de integración; documentar prerequisitos.
- **Compatibilidad de tooling:** estandarizar invocación npm (`npm.cmd`/`cmd`) y scripts cross-platform; considerar `.gitattributes`/EOL y paths para evitar sorpresas entre Windows dev y Ubuntu target.
- **PostgreSQL 18 e imagen:** confirmar tag/imagen soportada en el entorno real (homelab) y en CI/dev; fijar versión explícita en Compose al implementar.
- **Estructura objetivo:** mantener la propuesta de `AGENTS.md` como guía, creando solo lo necesario por etapa (sin carpetas vacías) y registrando desviaciones justificadas en docs.
- **Puertos/endpoints:** no preasignar; definir tras inventario real y exponer solo lo necesario (PostgreSQL nunca público).

---

## 5. Tareas ordenadas para comenzar la Fase 1

### Prerrequisitos / verificación previa (bloqueantes para partes de Fase 1)

1. **Inventario del homelab (Ubuntu, 16 GB, NPM/Portainer):** con acceso aprobado, listar contenedores, imágenes, redes, puertos publicados, volúmenes, uso de recursos y servicios existentes; identificar colisiones y reservar nombres/puertos/redes para DomiBase sin tocar stacks actuales.
2. **Habilitar y validar Docker en el entorno de desarrollo:** engine operativo, `docker compose version`, capacidad de ejecutar contenedores; documentar diferencias Windows vs homelab.
3. **Validaciones externas rápidas:** scope/nombre verificado y paquete publicado como `0.1.0` (`latest`); elegir imagen PostgreSQL 18 compatible y política de actualización.

### Fase 1 — orden de ejecución recomendado (ajustado a hallazgos)

4. **Cimentación del repositorio:** añadir `.gitignore` apropiado (.NET/Node/Docker/env), `.env.example` (solo placeholders) y documentación de prerequisitos/entorno (dev Windows + target homelab) sin secretos.
5. **Diseño técnico detallado (documentar antes de codificar):** contratos Control/Runtime, entidades de control (`Project`, `ApiKey`, `ProvisioningOperation`, `ExposedTable`, `AuditEntry`), máquina de estados de provisioning (idempotencia/compensación, `CREATE DATABASE` fuera de tx) y modelo de roles/grants/RLS por proyecto.
6. **Infraestructura local aislada:** Docker Compose de desarrollo (PostgreSQL con volumen nombrado, healthcheck, sin exposición pública), variables en `.env` (no versionado). Fijar versión de imagen tras validación.
7. **Backend — estructura de solución:** crear solución/proyectos .NET (Control/Runtime y capas/módulos según `AGENTS.md`) solo donde aporte valor inmediato; configurar build/test.
8. **Control Plane base:** base `domibase_control`, migraciones/metadatos, CRUD admin de proyectos, estados (`Provisioning/Ready/Failed/Deleting`), auditoría.
9. **API keys:** generación criptográfica, hash verificable (p. ej. HMAC-SHA-256 + pepper), mostrar valor completo solo al crear/rotar, listado seguro, revocación/rotación.
10. **Provisioning:** flujo idempotente/reintentable/compensable para DB y roles por proyecto (owner/migraciones separado, login Runtime mínimo, roles funcionales), sin superuser en Runtime.
11. **Runtime Plane:** middleware API key, resolución de proyecto/conexión (solo `Ready`), endpoints `GET /health` y CRUD de datos según contrato, validación allowlist, SQL parametrizado/identificadores seguros, transacciones cortas, `SET LOCAL ROLE` + claims locales, rate limits/límites, errores normalizados.
12. **Seguridad y RLS fase 1:** tablas expuestas con RLS, acceso anónimo limitado, demostrar INSERT/SELECT según políticas y aislamiento (proyectos, tablas no expuestas, claves inválidas/revocadas, sin fuga de contexto en pool).
13. **SDK TypeScript (`packages/client`):** `createClient`, query builder y transporte separados, filtros/orden/limit/range, tipos/errores, fetch inyectable/AbortSignal/timeout/SSR-safe; sin deps Supabase; pruebas de serialización/contrato.
14. **Panel mínimo (`frontend/admin`):** auth admin básica segura, crear/listar proyectos, ver estado/endpoint, gestionar tabla/columnas iniciales y claves (alcance mínimo viable).
15. **Ejemplo Astro:** consumir SDK propio para insertar/consultar `contactos` en `paneles-solares`.
16. **Verificación completa (DoD):** unit/integration/security tests (PostgreSQL real en contenedor), criterios de aceptación de `AGENTS.md`/fase 1, build limpio, docs actualizadas, despliegue reproducible y sin credenciales reales en Git.

---

## 6. Actualización propuesta para `docs/implementation/phase-01.md` (pendiente de aplicar)

Sugerencia de cambios a incorporar según hallazgos:

1. **Inspección — estado real:** repositorio limpio en `main` (1 commit, remoto configurado), solo docs/AGENTS/README presentes; sin NexStack en código; .NET `10.0.103`, Node `v24.13.1`, npm `11.8.0` (`npm.cmd`), Docker CLI `29.8.2` pero **daemon inaccesible** en la sesión local; `psql` no en `PATH`.
2. **Prerequisitos/bloqueos:** inventario obligatorio del homelab Ubuntu/NPM/Portainer antes de desplegar; habilitar Docker engine para dev/tests; confirmar acceso/recursos (16 GB) y ausencia de colisiones de puertos/redes/volúmenes/nombres.
3. **Ajustes de entorno:** inspección realizada en Windows; necesidad de scripts cross-platform (usar `npm.cmd` si aplica) y alineación con target Ubuntu.
4. **Infraestructura local:** fijar imagen PostgreSQL 18 validada, volumen nombrado, healthcheck, sin publicación externa; añadir `.gitignore` y `.env.example` como prerequisito temprano.
5. **Validaciones previas:** disponibilidad de `@domibase/client` resuelta con publicación `0.1.0`; registrar decisiones pendientes (puertos, estrategia de secretos/persistencia de claves de cifrado) con fecha/hallazgo.
6. **Trazabilidad:** anotar que contenedores/redes/volúmenes existentes no pudieron inventariarse localmente y deben relevarse en el homelab.

---

## Anexo — Fase 0/Fase 1 (2026-11-17)

- **Docker Desktop:** motor Linux operativo verificado (`docker info` OK).
- **Stacks existentes detectados (no modificados):** `postgres:15` (puerto 5432), `redis`, `evolution_api`, `api-container`, `sic_*`, `mr_menus_*`, `finanxas-*` (exited/up según el caso).
- **Conflicto de puerto:** `5432` ocupado por `postgres:15`. `55432` descartado por caer en el rango reservado de Windows/Hyper-V `55375–55474`. Se eligió `5433` (libre y fuera de rangos).
- **PostgreSQL 18:** `postgres:18-alpine` (18.7) levantado como `domibase-postgres`, healthy en `127.0.0.1:5433`, volumen `domibase_postgres_data` montado en `/var/lib/postgresql` (layout PostgreSQL 18+).
- **Base de control:** `domibase_control` creada por el entrypoint.
- **Persistencia:** verificada con base de smoke test `domibase_smoke_check` (fila `1|ok` conservada tras `docker compose stop` + `up -d`).
- **Restricciones respetadas:** solo PostgreSQL en Docker; sin API/SDK/Astro en contenedores; sin `docker compose down -v`; sin eliminar volúmenes/recursos; sin commits.
