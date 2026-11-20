# Desarrollo local

## Principio
El desarrollo local admite dos modos:

- **Modo clásico:** solo PostgreSQL se ejecuta en Docker; la API, el SDK, los tests y el ejemplo Astro se ejecutan directamente en Windows.
- **Modo stack Docker (esta PC):** `docker compose up -d --build` levanta `domibase-postgres`, `domibase-api` y `domibase-admin` bajo el mismo proyecto `domibase`. Astro sigue en Windows y consume la API en `http://localhost:5080`.

No se publican imágenes ni se modifica ningún otro stack de la PC.

## 1. PostgreSQL en Docker

```powershell
# Copiar variables locales (una vez)
Copy-Item .env.example .env
# Editar .env con valores locales seguros

docker compose up -d
docker compose ps
docker compose logs -f postgres
```

Contenedor: `domibase-postgres` (imagen `postgres:18-alpine`; verificado PostgreSQL 18.7).
Puerto local: `127.0.0.1:5433` (el `5432` local está ocupado por otro stack; `55432` se descartó por caer en un rango reservado de Windows/Hyper-V).
Base de control: `domibase_control`.
Volumen persistente: `domibase_postgres_data`, montado en **`/var/lib/postgresql`**.

> PostgreSQL 18 cambió el layout oficial: el volumen debe montarse en `/var/lib/postgresql` (no en `/var/lib/postgresql/data`). El entrypoint crea el cluster en `/var/lib/postgresql/18/docker`.

Detener sin borrar datos:
```powershell
docker compose stop
```

Verificación de persistencia realizada en Fase 1: se creó la base de smoke test `domibase_smoke_check` con una tabla y una fila; tras `docker compose stop` + `docker compose up -d` la fila seguía presente (`1|ok`). La base de smoke test se conserva y puede eliminarse manualmente en el futuro con autorización.

**Nunca** ejecutar `docker compose down -v` ni eliminar volúmenes existentes.

## 2. Variables de entorno
`.env.example` contiene placeholders. El `.env` real no se versiona (ver `.gitignore`).

Variables principales:
- `DOMIBASE_POSTGRES_PORT` (por defecto `5433`).
- `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_CONTROL_DB`.
- `CONTROL_DB_CONNECTION_STRING` (metadatos; obligatoria al arrancar la API).
- `PROVISIONING_DB_CONNECTION_STRING` (administrativa, solo provisioning; obligatoria al arrancar).
- `DOMIBASE_ADMIN_TOKEN` (mínimo 16 caracteres; obligatoria al arrancar).
- `DOMIBASE_API_KEY_PEPPER` (mínimo 16 caracteres; obligatoria desde Fase 3; usar un valor aleatorio largo). Cambiarlo invalida todos los hashes de claves existentes.
- `DOMIBASE_SECRETS_KEY` (obligatoria desde Fase 4): clave AES-256 en base64 que debe decodificar exactamente 32 bytes. Cifra las contraseñas de Runtime en `domibase_control`. Generar con `[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`. Cambiarla invalida los ciphertexts existentes.
- `RUNTIME_DB_HOST` / `RUNTIME_DB_PORT` (por defecto `127.0.0.1:5433`; el puerto cae a `DOMIBASE_POSTGRES_PORT`).
- `RUNTIME_DB_SSL_MODE` (`Prefer` en desarrollo; endurecer en despliegue).
- `RUNTIME_DB_MAX_POOL_SIZE` (20), `RUNTIME_DB_CONNECT_TIMEOUT_SECONDS` (10), `RUNTIME_DB_COMMAND_TIMEOUT_SECONDS` (15).
- `RUNTIME_MAX_BODY_BYTES` (1 MiB), `RUNTIME_MAX_INSERT_ROWS` (100).
- `RUNTIME_RATE_LIMIT_PERMITS` (300, rango 1–10000) y `RUNTIME_RATE_LIMIT_WINDOW_SECONDS` (60, rango 1–3600): defaults globales del limitador; un valor fuera de rango impide el arranque. Se pueden sobrescribir por proyecto con `PATCH /api/v1/projects/{id}/rate-limit`; `null` hereda estos defaults (`docs/implementation/p1-4-rate-limits.md`).
- `DOMIBASE_CORS_ORIGINS` (opcional; orígenes separados por comas).
- `DOMIBASE_API_URL` para SDK/Astro.

La API carga automáticamente el `.env` más cercano en el directorio del proyecto o sus padres. Las variables de entorno reales y `appsettings` siempre tienen prioridad; `.env` solo rellena claves ausentes.

## 3. API y migraciones (Windows)

### Migración de metadatos
`domibase_control` usa EF Core. La herramienta `dotnet-ef` está fijada localmente en `dotnet-tools.json` (10.0.15); no uses una instalación global 8.x.

```powershell
dotnet tool restore
dotnet ef database update --project backend/src/DomiBase.Infrastructure --startup-project backend/src/DomiBase.Api
```

La API **no aplica migraciones automáticamente** al arrancar. Ejecuta el comando cuando cambie el modelo de control.

### Ejecución
```powershell
dotnet run --project backend/src/DomiBase.Api
```
- Escucha en `http://localhost:5080` (ver `Properties/launchSettings.json`).
- OpenAPI en `http://localhost:5080/openapi/v1.json` (solo Development).
- `GET /health` — liveness.
- `GET /health/ready` — readiness; comprueba PostgreSQL del plano de control.
- `/api/v1/*` — Control Plane real (requiere `X-DomiBase-Admin-Token: <token>`): proyectos, provisioning y API keys.
- `/rest/v1/{table}` — Runtime Plane real: CRUD con `apikey: db_public_...`, filtros, orden, límites, RLS y aislamiento.

Ejemplo de Control Plane:
```powershell
$token = "<valor de DOMIBASE_ADMIN_TOKEN>"
$headers = @{ "X-DomiBase-Admin-Token" = $token }

# Crear proyecto (provisiona base y roles)
$project = Invoke-RestMethod http://localhost:5080/api/v1/projects -Method Post -Headers $headers `
  -ContentType "application/json" -Body '{"slug":"paneles-solares","name":"Paneles Solares"}'
$project.id
$project.status      # Ready o Failed (con failureReason)

# Crear API key pública: el valor completo solo se devuelve aquí
$key = Invoke-RestMethod "http://localhost:5080/api/v1/projects/$($project.id)/keys" -Method Post `
  -Headers $headers -ContentType "application/json" -Body '{"label":"local"}'
$key.key

# Reintentar provisioning de un proyecto Failed
Invoke-RestMethod "http://localhost:5080/api/v1/projects/$($project.id)/provision" -Method Post -Headers $headers

# Revocar (idempotente)
Invoke-RestMethod "http://localhost:5080/api/v1/projects/$($project.id)/keys/$($key.id)" -Method Delete -Headers $headers

# Cupo de Runtime del proyecto (null en ambos campos restaura el default global)
Invoke-RestMethod "http://localhost:5080/api/v1/projects/$($project.id)/rate-limit" -Method Patch `
  -Headers $headers -ContentType "application/json" -Body '{"permits":20,"windowSeconds":60}'
```

Ejemplo de Runtime (la clave pública solo se muestra al crearla):
```powershell
$runtimeHeaders = @{ apikey = $key.key }

# Insertar
Invoke-RestMethod http://localhost:5080/rest/v1/contactos -Method Post -Headers $runtimeHeaders `
  -ContentType "application/json" -Body '{"nombre":"Ana","email":"ana@example.test"}'

# Seleccionar con filtro y orden
Invoke-RestMethod "http://localhost:5080/rest/v1/contactos?select=id,nombre&f=nombre.eq.Ana" -Headers $runtimeHeaders

# Actualizar/eliminar exigen al menos un filtro
Invoke-RestMethod "http://localhost:5080/rest/v1/contactos?f=email.eq.ana@example.test" -Method Patch `
  -Headers $runtimeHeaders -ContentType "application/json" -Body '{"nombre":"Ana María"}'
Invoke-RestMethod "http://localhost:5080/rest/v1/contactos?f=email.eq.ana@example.test" -Method Delete -Headers $runtimeHeaders
```

> Las tablas del proyecto deben crearse con `GRANT` y RLS habilitado; una tabla sin RLS no se expone (404) y una tabla sin política devuelve SELECT vacío y deniega escrituras. Las tablas se crean desde `/api/v1/projects/{id}/schema`; desde el panel se pueden explorar filas como `anon` y administrar políticas RLS con plantillas allowlisted (P2.1).

En modo clásico la API se conecta a PostgreSQL por el puerto publicado (`127.0.0.1:5433`). En modo stack Docker usa el nombre de servicio `postgres` y el puerto interno `5432` de la red `domibase_net`; Compose sobrescribe las cadenas de conexión del `.env`. El arranque falla de forma explícita si falta una cadena de conexión, el token administrativo, el pepper o la clave de secretos.

## 4. Tests backend
```powershell
dotnet build backend/DomiBase.sln
dotnet test backend/DomiBase.sln
```

Estado en Fase 4: build limpio (0 warnings) y **98 tests** en verde (73 unitarios, 19 integración, 6 seguridad). Los tests de integración requieren PostgreSQL real, el `.env` local, `DOMIBASE_API_KEY_PEPPER` y `DOMIBASE_SECRETS_KEY`; crean y eliminan bases/roles `it-*` y metadatos temporales, y se ejecutan en serie. No se usa SQLite ni mocks para comportamiento PostgreSQL. Los tests de seguridad no requieren base de datos.

## 5. SDK (Fase 5, completado)
```powershell
cd packages/client
cmd /c npm install
cmd /c npm test        # build TypeScript + 50 tests
cmd /c npm run build
```

- Paquete `@domibase/client@0.1.0` publicado en npm con tag `latest`; el ejemplo Astro consume directamente el registry, sin dependencias locales ni scripts de build del SDK.
- `createClient`, `from`, query builder inmutable, `select`/`insert`/`update`/`delete`, filtros, `order`, `limit`, `range`, `abortSignal`, `single` y `maybeSingle`.
- `fetch` inyectable, timeout y `AbortSignal`; compatible con Node.js, SSR y navegador sin acceder a `window` al importar.
- Los tests usan `node --test` con un `fetch` simulado; la integración contra la API real y Astro se hace en la Fase 6.
- Detalle del contrato y de la semántica en `docs/implementation/phase-05.md` y `docs/sdk/api-contract.md`.

En PowerShell pueden estar bloqueados `npm.ps1`/`npx.ps1`; usar `npm.cmd`/`npx.cmd` o `cmd /c npm ...`.

## 6. Ejemplo Astro (Fase 6, completado)

### Opción A — stack Docker local (PostgreSQL + API + panel)
```powershell
# Desde la raíz del repositorio
docker compose up -d --build
docker compose ps
# API: http://127.0.0.1:5080 · Panel: http://127.0.0.1:5190

# Preparar proyectos, tablas/RLS, claves y .env del ejemplo
node scripts/dev/phase-06-demo.mjs

cd examples/astro-example
cmd /c npm install
cmd /c npm run dev        # http://localhost:4325
```

### Opción B — modo clásico (solo PostgreSQL en Docker)
```powershell
# API en una terminal
dotnet run --project backend/src/DomiBase.Api

# Preparar proyectos, tablas/RLS, claves y .env del ejemplo
node scripts/dev/phase-06-demo.mjs

cd examples/astro-example
cmd /c npm install
cmd /c npm run dev        # http://localhost:4325
```

- `examples/astro-example` consume la API local con `@domibase/client@0.1.0` desde npm en SSR (`output: "server"`, adaptador `@astrojs/node` standalone); cubre solo la API Fase 1.
- `scripts/dev/phase-06-demo.mjs` crea/reutiliza `paneles-solares` y `aislamiento-demo`, genera claves, aplica `contactos` con RLS, siembra filas y escribe `examples/astro-example/.env` (sin imprimir secretos). No crea bucket ni función de demo.
- `scripts/dev/phase-06-verify-astro.mjs` verifica los formularios y la página `/diagnostico` contra el servidor en ejecución.
- Puerto fijo **4325**: el `4321` por defecto puede estar ocupado por otra aplicación local.
- Las claves viven solo en el servidor de Astro (`astro:env/server`, `access: "secret"`); nunca en el navegador ni con prefijo `PUBLIC_`.
- Comandos útiles: `cmd /c npm run check` (tipos), `cmd /c npm run build` (standalone), `cmd /c "set PORT=4325&& npm run start"` (producción con `.env`).
- Detalle en `examples/astro-example/README.md` y `docs/implementation/phase-06.md`.

## 7. Validación post-reinicio (Fase 7)

Tras `docker compose stop` + `docker compose up -d` (nunca `down -v`), arranca la API y ejecuta:

```powershell
node scripts/dev/phase-07-validate.mjs
```

Verifica, sin modificar claves ni datos, que las filas de demo persisten, los roles/bases siguen presentes, `domibase_control` está bloqueado desde el Runtime y las claves inválida/revocada/ausente se rechazan. Detalle en `docs/implementation/phase-07.md`.

## 8. Imagen futura (no durante desarrollo)
La imagen `bladdy34/domibase-api:0.1.0` tiene su Dockerfile preparado en `infrastructure/docker/Dockerfile` (multi-stage, usuario no root, bases `10.0-noble` fijadas por digest, healthcheck y `.dockerignore`). Diseño y variables en `infrastructure/docker/README.md` y `docs/implementation/phase-08.md`.

Validación estática (no construye la imagen):
```powershell
docker buildx build --check -f infrastructure/docker/Dockerfile .
```

**No ejecutar `docker build` ni publicar sin completar pruebas y obtener autorización.**

## 9. Panel privado (alcance mínimo)

Solo PostgreSQL sigue en Docker; el panel se ejecuta en Windows:

```powershell
# Terminal 1
dotnet run --project backend/src/DomiBase.Api

# Terminal 2
cd frontend/admin
cmd /c npm install
cmd /c npm run dev        # http://127.0.0.1:5190
```

Introduce `DOMIBASE_ADMIN_USERNAME` y `DOMIBASE_ADMIN_PASSWORD` del `.env` local. `POST /admin/v1/login` devuelve una sesión `dbsess_` caducable guardada únicamente en `sessionStorage`; `DOMIBASE_ADMIN_TOKEN` queda reservado a servicios/tests. Vite proxya `/api` y `/admin` a `http://127.0.0.1:5080` (el login está en `/admin/v1/login`); para cambiar el destino usa `CONTROL_API_TARGET` en `frontend/admin/.env.local` (no versionado). El panel tiene solo **Proyectos** (crear, abrir, editar cupo, eliminar) y, dentro de proyecto, **Resumen**, **Table Editor** y **Conexión**. Detalle en `frontend/admin/README.md` y `docs/implementation/admin-minimal-scope.md`.

### Verificación E2E del alcance mínimo

Con la API en marcha y el SDK compilado:

```powershell
node scripts/dev/verify-minimal-admin.mjs
docker restart domibase-postgres
node scripts/dev/verify-minimal-admin.mjs --after-restart
```

La primera pasada cubre 14/14 comprobaciones; la segunda, 15/15, e incluye persistencia de filas. Si se reinicia PostgreSQL, espera el health check y reinicia también la API para descartar conexiones pooled obsoletas. El registro completo está en `docs/implementation/admin-minimal-e2e.md`.

Para validar el formulario Astro con el SDK propio:

```powershell
npm run dev --prefix examples/astro-example
node scripts/dev/phase-06-verify-astro.mjs http://localhost:4325
```

Para los backups, la API exige `BACKUP_ROOT` (directorio dedicado fuera del repositorio). En Windows no hay `pg_dump`/`pg_restore` 18 en el `PATH`, así que el entorno local usa los binarios del contenedor:

```dotenv
BACKUP_ROOT=./backups
BACKUP_TOOL_MODE=docker
BACKUP_DOCKER_CONTAINER=domibase-postgres
```

Los volcados se escriben bajo `{BACKUP_ROOT}/{projectId}/{backupId}.dump`; no se versionan. El alcance y el contrato completo están en `docs/implementation/p1-6-backups.md`.

Verificación E2E del cupo (crea y elimina dos proyectos temporales; no imprime token ni claves):

```powershell
node scripts/dev/verify-rate-limits-panel.mjs   # 14/14 comprobaciones
```

Requiere la API y el panel en marcha; el límite se aplica a REST, Storage, Functions, Auth y al handshake de Realtime, no a los frames WebSocket.

Verificación E2E de backups (crea un proyecto temporal con una tabla y su política RLS, inserta una fila vía Runtime, crea y descarga un volcado desde el panel, lo muta, restaura tras escribir el `slug`, comprueba la fila recuperada y elimina todo; no imprime secretos ni contenido del dump):

```powershell
node scripts/dev/verify-backups-panel.mjs   # 18/18 comprobaciones
```

Además de la API y el panel, requiere `BACKUP_ROOT`, `BACKUP_TOOL_MODE=docker` y `BACKUP_DOCKER_CONTAINER=domibase-postgres` en el entorno de la API.

Verificación E2E del visor de auditoría (si faltan, el script arranca la API compilada y el panel con `npm run dev`; crea 21 claves, comprueba filtros, paginación, redacción y pasos de provisioning, guarda capturas y elimina el proyecto temporal; no imprime secretos):

```powershell
node scripts/dev/verify-audit-panel.mjs   # 16/16 comprobaciones
```

Requiere que la migración `20261118045153_AddAuditProjectId` esté aplicada a `domibase_control`. Las capturas quedan en `.impeccable/review/audit-desktop.png` y `audit-mobile.png`.

## Solución de problemas
- Si `docker info` falla: iniciar Docker Desktop y confirmar motor Linux.
- Si el puerto `5433` está ocupado: cambiar `DOMIBASE_POSTGRES_PORT` en `.env` y documentarlo (evitar rangos reservados por Windows/Hyper-V).
- Si `dotnet ef` falla por versión: ejecutar `dotnet tool restore` desde la raíz (el repo fija 10.0.15 en `dotnet-tools.json`).
- Si la API no arranca por `DOMIBASE_ADMIN_TOKEN`, `DOMIBASE_API_KEY_PEPPER` o `DOMIBASE_SECRETS_KEY`: revisar `.env`; son obligatorias (token/pepper mínimo 16 caracteres; la clave de secretos debe decodificar exactamente 32 bytes en base64).
- Si Runtime devuelve 503 `PROJECT_UNAVAILABLE`: el ciphertext de la contraseña no se pudo descifrar (clave de secretos distinta). Reintentar el provisioning del proyecto regenera las credenciales.
- Si Runtime devuelve 404 en una tabla existente: confirmar que está en `public`, con RLS habilitado y `GRANT` para el rol `_anon`.
- Si el provisioning falla por permisos: el usuario administrativo necesita `CREATEDB`/`CREATEROLE` y acceso a `domibase_control`. En local es superuser; en despliegue debe reducirse a lo mínimo necesario.
- Si la API no arranca por `BACKUP_ROOT`: defínela en `.env` o en el entorno con una ruta dedicada; es obligatoria desde P1.6. En modo `docker`, `BACKUP_DOCKER_CONTAINER` debe nombrar un contenedor PostgreSQL con `pg_dump`/`pg_restore` 18.
- Si fallan los tests de integración: confirmar PostgreSQL en `5433` y un `.env` completo; no ejecutarlos en paralelo (la asamblea ya deshabilita la paralelización).
- Si npm falla por firma de scripts: usar `npm.cmd`.
- Si PostgreSQL no responde: revisar `docker compose ps` y `docker compose logs postgres`; no borrar el volumen.
