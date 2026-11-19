# Auditoría técnica integral de DomiBase

**Fecha:** 2026-11-17  
**Propósito:** documentar exactamente qué está desarrollado, cómo está construido y qué capacidades ofrece actualmente, como insumo para una comparación posterior e independiente contra Supabase.  
**Alcance de la comparación:** esta auditoría **no compara** DomiBase con Supabase; solo inventaria y clasifica el estado actual.

## 0. Metodología y limitaciones

Auditoría de **solo lectura**. No se modificó código, no se instalaron dependencias, no se actualizaron paquetes, no se ejecutaron migraciones y no se cambió configuración. El único archivo creado es este informe.

Clasificación usada:

| Símbolo | Significado |
| --- | --- |
| ✅ **Implementado** | Existe código real, está cableado y hay evidencia de uso en rutas, servicios o pruebas. |
| ◐ **Parcial** | Existe, pero con huecos relevantes frente a la capacidad descrita. |
| ⛔ **No implementado** | No hay código que lo implemente. |
| 📄 **Solo planificado/documental** | Aparece en documentación, enumeraciones o nombres, sin implementación efectiva. |

Regla aplicada: **no se considera implementado solo por existir una clase, interfaz, endpoint o componente con ese nombre**. Cuando una afirmación solo proviene de documentación, se marca como tal.

> El enunciado de la solicitud terminaba en «E. API → Paginación». Se auditsaron las secciones A–E recibidas y se añadieron F–J para cubrir SDK, ejemplo Astro, panel, pruebas y operaciones. No se reprodujo ningún valor secreto de `.env`.

> **Actualización 2026-11-18:** este documento conserva el estado de cuando se emitió. Desde entonces se resolvieron P0.1–P0.4, P1.1–P1.7 y P2.1, y después el panel se simplificó al alcance mínimo `Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK`. Las clasificaciones ⛔/◐ de las secciones 3–6 deben leerse como históricas; la fuente vigente es `docs/implementation/admin-minimal-scope.md`, con registro E2E en `docs/implementation/admin-minimal-e2e.md`. Estado actual: **491/491 tests backend** (311 unitarios, 134 de integración y 46 de seguridad), **70/70 tests del SDK** y **57/57 tests del panel**; E2E del flujo mínimo 14/14 y 15/15 tras reiniciar PostgreSQL, y CRUD de Astro verificado.

---

## 1. Resumen ejecutivo

DomiBase es un **modular monolith** en ASP.NET Core 10 con dosplanes lógicos separados:

- **Control Plane privado** bajo `/api/v1`, protegido por token administrativo.
- **Runtime Plane público** bajo `/rest/v1`, `/auth/v1`, `/storage/v1`, `/functions/v1` y `/realtime/v1`, resuelto por API key pública y, opcionalmente, Bearer.

La plataforma tiene implementadas las capacidades centrales: proyectos con base PostgreSQL independiente, provisioning idempotente, API keys públicas, CRUD con allowlists/RLS, autenticación propia, realtime, storage, funciones WASM, SDK TypeScript, ejemplo Astro y panel administrativo. La última entrega añadió un inspector de esquema de solo lectura.

Las ausencias más relevantes para una comparación son:

- No hay **eliminación de proyectos** ni configuración genérica por proyecto.
- No hay **rotación** de API keys ni claves secretas emitibles; el generador las conoce pero `ProjectService` rechaza `kind=secret`.
- No hay **compensación real** de provisioning; el estado existe solo como enumeración.
- No hay **DDL administrativo** para crear tablas/columnas/índices/políticas; las políticas RLS demostradas se crean en fixtures de test y en el script de demo, no desde la Control API.
- ~~No hay **backups/restauración**, extensiones PostgreSQL ni PgBouncer.~~ Histórico: backups/restauración y extensiones resueltos en P1.6 y P1.5 (`docs/implementation/p1-6-backups.md`, `docs/implementation/p1-5-extensions.md`); PgBouncer sigue sin implementarse.
- No hay **recuperación de contraseña, verificación de email, OAuth, MFA**, logout global ni gestión administrativa de sesiones.
- La API REST no genera endpoints automáticamente y no expone `range` HTTP; ofrece `limit`/`offset` y un `count` de filas afectadas/devueltas, no un total.
- ~~El rate limiting existe, pero es **por IP**, no por proyecto.~~ Histórico: resuelto en P1.4 (`docs/implementation/p1-4-rate-limits.md`).
- La imagen Docker está preparada, pero **no construida ni publicada**; no hay despliegue en homelab.

---

## 2. Inventario del repositorio

### 2.1 Soluciones y proyectos

`backend/DomiBase.sln` contiene 10 proyectos (`backend/DomiBase.sln:6-29`):

| Proyecto | Rol |
| --- | --- |
| `DomiBase.Domain` | Entidades, enums, naming y contratos de dominio. |
| `DomiBase.Application` | Servicios de aplicación y contratos; no referencia Npgsql. |
| `DomiBase.Infrastructure` | EF Core, Npgsql, implementaciones de repositorios, auth, runtime, storage, functions, realtime. |
| `DomiBase.Provisioning` | Conexión privilegiada, creación de bases/roles, lector de catálogo. |
| `DomiBase.Api` | Host ASP.NET Core, middleware, endpoints y filtros HTTP. |
| `DomiBase.Functions.Worker` | Worker Wasmtime one-shot. |
| `DomiBase.UnitTests` / `IntegrationTests` / `SecurityTests` | Suites backend. |
| `DomiBase.Functions.TestWorker` | Worker auxiliar de pruebas. |

### 2.2 Lenguajes y frameworks

| Capa | Tecnología |
| --- | --- |
| Backend | C# / .NET 10, ASP.NET Core 10, EF Core 10 + Npgsql, SignalR, Wasmtime. |
| Base de datos | PostgreSQL 18 (`postgres:18-alpine`). |
| SDK | TypeScript ESM, Node ≥20, `@microsoft/signalr` como dependencia real. |
| Ejemplo | Astro 7 SSR + `@astrojs/node` standalone. |
| Panel | React 19 + Vite 8 + TypeScript estricto. |
| Tests | xUnit, Vitest, `node --test`, scripts `.mjs` de verificación. |

### 2.3 Arquitectura y dependencias

- Un único host ASP.NET Core; no hay dos APIs separadas en Fase 1.
- `Application` define contratos; `Infrastructure` y `Provisioning` los implementan.
- `Api` compone DI y mapea endpoints en `Program.cs:22-31,128-137`.
- El Runtime nunca recibe el `projectId` desde el cliente: lo resuelve por hash de API key (`RuntimeProjectResolver.cs:21-67`).
- La cadena privilegiada de provisioning está confinada a `DomiBase.Provisioning`; el inspector de esquema la reutiliza con transacción `READ ONLY`.

### 2.4 Infraestructura

- `docker-compose.yml`: **único servicio** `postgres`, imagen `postgres:18-alpine`, `127.0.0.1:5433`, volumen `domibase_postgres_data`, healthcheck `pg_isready`.
- `infrastructure/docker/Dockerfile`: multi-stage, bases `10.0-noble` fijadas por digest, usuario no root, healthcheck `/health`. Comentario explícito: preparado pero no construido/publicado.
- No hay `infrastructure/postgres` con scripts SQL; no hay nginx ni manifiestos de despliegue.
- `.env.example` sin secretos reales; `.env` local gitignoreado.

### 2.5 Documentación

Existen 30 documentos bajo `docs/`, además de `README.md`, `PRODUCT.md` y `DESIGN.md`. La documentación más reciente y consistente con el código es:

- `docs/implementation/roadmap.md`
- `docs/implementation/admin-panel-plan.md`
- `docs/implementation/schema-inspection.md`
- `docs/architecture/overview.md`

Se detectaron desactualizaciones documentales; véase §9.

---

## 3. Auditoría funcional

### A. Gestión de proyectos

| Capacidad | Estado | Evidencia y observación |
| --- | --- | --- |
| Creación de proyectos | ✅ | `POST /api/v1/projects` en `ControlEndpoints.cs:34-44`; validación de slug/nombre y unicidad en `ProjectService.cs:34-62`. |
| Listado y obtención | ✅ | `ControlEndpoints.cs:18-32`. |
| Identificadores | ✅ | `Project.Id` GUID; slug validado; nombre de base derivado `domibase_<slug>` (`ProjectNaming.cs`). |
| Credenciales por proyecto | ✅ | Password del login `_runtime` cifrado con AES-GCM antes de persistir (`ProjectService.cs:72-84`). |
| Configuración independiente | ◐ | Hay metadatos reales, estado, base, credenciales, claves y operaciones; **no hay API genérica de configuración por proyecto**. |
| Estados | ✅ | `Provisioning`, `Ready`, `Failed`, `Deleting` en `ProjectStatus`; uso real en `ProjectService.cs:69,99-109,237`. |
| Reintento de provisioning | ✅ | `POST /projects/{id}/provision` (`ControlEndpoints.cs:46-55`). |
| Eliminación de proyectos | ⛔ | No existe `DELETE /api/v1/projects/{id}`. `Deleting` solo se menciona para rechazar reintentos (`ProjectService.cs:104-106`). |
| API keys públicas | ✅ | Crear/listar/revocar en `ControlEndpoints.cs:57-93`. |
| API keys secretas | ◐ | Prefijo y generación preparados (`ApiKeyGenerator.cs:13-16`), pero `ProjectService.cs:305-308` rechaza `kind=secret`. |
| Rotación de API keys | ⛔ | No hay método ni endpoint; solo creación y revocación. |
| Aislamiento entre proyectos | ✅ | Base, roles y credenciales por proyecto; verificado en tests de integración y seguridad. |
| Gestión de recursos / cuotas comerciales | ⛔ | No hay billing, planes ni cuotas comerciales; sí hay límites técnicos de Runtime (§3D y §3E). |
| Administración centralizada | ✅ | Control API + panel privado; solo el propietario. |

### B. Base de datos

| Capacidad | Estado | Evidencia y observación |
| --- | --- | --- |
| Integración PostgreSQL | ✅ | EF Core + Npgsql para metadatos; Npgsql directo para Runtime y provisioning. |
| Base independiente por proyecto | ✅ | `domibase_<slug>` con owner dedicado (`ProjectProvisioner.cs:31-35,83-84,164-188`). |
| Creación de bases y roles | ✅ | Roles `owner`, `anon`, `authenticated` y login `_runtime` con `LOGIN NOINHERIT NOBYPASSRLS` (`ProjectProvisioner.cs:119-162`). |
| Idempotencia y reintento | ✅ | Advisory lock y comprobaciones de estado existente (`ProjectProvisioner.cs:47-57,104-107,124-127`). |
| Compensación | 📄 | `ProvisioningStatus.Compensated` existe en `ProvisioningStatus.cs`, sin lógica real. |
| Administración de esquemas | ◐ | Inspector de catálogo de solo lectura; **sin mutación de esquema**. |
| Tablas / relaciones / índices / constraints | ◐ | Migraciones EF Core reales para `domibase_control` y tablas de dominio; esquemas de demo creados por script; no hay motor de migraciones por proyecto. |
| Migraciones | ✅ para control / ⛔ para proyectos | Cinco migraciones EF en `Persistence/Migrations/`: `InitialControlPlane`, `AddRuntimeCredentials`, `AuthSigningKeys`, `StorageBucketsAndObjects`, `Functions`. No hay `.sql` versionado. |
| SQL parametrizado y quoting seguro | ✅ | `RuntimeQueryBuilder.cs`: valores parámetros, identificadores desde catálogo. |
| RLS en producción | ◐ | Mecanismo implementado y demostrado; las políticas de negocio se crean en fixtures de test y en `phase-06-demo.mjs`, no desde provisioning ni panel. |
| Extensiones PostgreSQL | ✅ desde P1.5 | Histórico: no había `CREATE EXTENSION`; ahora hay allowlist de diez extensiones `trusted` con DDL tipado e idempotente. Ver `p1-5-extensions.md`. |
| Backups / restauración | ✅ desde P1.6 | Histórico: ⛔, solo persistencia por volumen y verificación post-reinicio. Ahora `pg_dump --format=custom`/`pg_restore` por proyecto, SHA-256 verificado, restauración transaccional confirmada, descarga/borrado y pestaña en el panel. Ver `p1-6-backups.md`. |
| Connection pooling | ✅ | Npgsql `Pooling=true`, `NoResetOnClose=false`, pool por credencial/base (`RuntimeConnectionFactory.cs:16-35`); `RUNTIME_DB_MAX_POOL_SIZE`, default 20. No hay PgBouncer. |

### C. Autenticación

| Capacidad | Estado | Evidencia y observación |
| --- | --- | --- |
| Registro | ✅ | `POST /auth/v1/register`; hash Argon2id (`AuthService.cs:19-45`). |
| Login | ✅ | `POST /auth/v1/login`; error uniforme para usuario/contraseña/habilitación (`AuthService.cs:47-69`). |
| Logout | ✅ | `POST /auth/v1/logout` (`AuthEndpoints.cs:36-40`). |
| JWT | ✅ | Access tokens ES256 por proyecto (`JwtAccessTokenService.cs`); clave de firma cifrada en control. |
| Refresh tokens | ✅ | Rotación y revocación de familia por reuso (`AuthService.cs:72-133`). |
| Bearer en Runtime/Storage/Functions/Realtime | ✅ | Siempre acompañado de `apikey` válido. |
| Recuperación de contraseña | ⛔ | Sin código. |
| Verificación de correo | ⛔ | Sin código. |
| Gestión de usuarios | ◐ | Registro y credenciales en esquema `auth`; no hay CRUD administrativo de usuarios. |
| Sesiones | ◐ | Refresh-token families, sesión en memoria del SDK y cookies httpOnly en Astro; no hay listado/ revocación global por usuario. |
| OAuth | ⛔ | Sin código. |
| MFA | ⛔ | Sin código. |
| Magic links | ⛔ | Sin código. |

### D. Autorización y seguridad

| Control | Estado | Evidencia y observación |
| --- | --- | --- |
| Protección Control Plane | ✅ | `AdminAuthenticationMiddleware`, `X-DomiBase-Admin-Token` o Bearer (`Program.cs:92`). |
| Separación Control/Runtime | ✅ | Rutas administrativas nunca bajo `/rest/v1`; tests de seguridad lo verifican. |
| Resolución server-side de proyecto | ✅ | Hash de clave pública, no `projectId` del cliente. |
| Clave inválida/revocada | ✅ | Rechazo en `RuntimeProjectResolver.cs:43-48`. |
| Exigencia de estado `Ready` | ✅ | `RuntimeProjectResolver.cs:53-56`. |
| Roles PostgreSQL | ✅ | Funcionales derivados del proyecto; el cliente nunca elige el rol SQL. |
| GRANT mínimos | ✅ | `REVOKE ALL FROM PUBLIC`, `GRANT CONNECT`, `GRANT USAGE`, grants por tabla/columna. |
| RLS y claims por transacción | ✅ | `SET LOCAL ROLE` + `set_config(..., true)` en `RuntimeDataService.cs:158-220`. |
| Aislamiento en pool | ✅ | `NoResetOnClose=false`; test `PooledConnections_DoNotLeakRoleOrClaims`. |
| Allowlists y parametrización | ✅ | Columnas/filtros/orden validados contra catálogo; valores parametrizados. |
| Sin SQL arbitrario público | ✅ | Superficie limitada a CRUD validado; no hay endpoint de SQL. |
| Secretos | ✅ con pendientes | `.env` gitignoreado, pepper HMAC, AES-GCM para credenciales, redacción en logs; rotación/backup de claves maestras pendiente documental. |
| Rate limiting | ✅ desde P1.4 | Histórico: `429/RATE_LIMITED` por IP (`Program.cs`); ahora cuota por proyecto con override en `projects` y fallback por IP, en memoria del único host (`docs/implementation/p1-4-rate-limits.md`). |
| Límites de payload | ✅ | `IHttpMaxRequestBodySizeFeature` para `/rest/v1` y `/functions/v1` (`Program.cs:99-126`). |
| Auditoría | ✅ para Control | Entradas para proyectos, claves, buckets, funciones y lecturas de esquema; no se auditó una auditoría por fila de datos Runtime. |
| CORS | ✅ correctamente no usado como seguridad | `Program.cs:66-86`; el código advierte explícitamente que CORS no es autorización. |

### E. API

| Capacidad | Estado | Evidencia y observación |
| --- | --- | --- |
| REST | ✅ | `GET/POST/PATCH/DELETE /rest/v1/{table}` (`RuntimeEndpoints.cs:20-28`). |
| Generación automática de endpoints | ⛔ | Todos los endpoints están mapeados manualmente en `Program.cs`. |
| CRUD | ✅ | Implementado en `RuntimeDataService` con validación de privilegios por columna. |
| Filtros | ✅ | `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is` (`RuntimeQueryBuilder.cs:22-25`). |
| Orden | ✅ | Hasta 3 ordenaciones (`RuntimeQueryBuilder.cs:220-231` en SDK; backend `order` validado). |
| Selección de columnas | ✅ | `select` validado contra columnas con privilegio. |
| Límites | ✅ | `limit` 1..1000; `offset` 0..1.000.000; topes de filtros/columnas/valores `in`. |
| Paginación / `range` | ◐ | Backend expone `limit`/`offset`; el SDK ofrece `range()` mapeando a ambos. **No existe parámetro HTTP `range`**. |
| `count` | ◐ | `{ data, count }` donde `count` son filas devueltas/afectadas, no `COUNT(*)` total. |
| `PATCH`/`DELETE` acotados | ✅ | Requieren filtros (`RuntimeQueryBuilder.cs:126-159`). |
| Errores canónicos | ✅ | `{ error: { code, message, details?, hint? } }` (`ApiError.cs`). |
| OpenAPI | ✅ en Development | `app.MapOpenApi()` solo en entorno de desarrollo (`Program.cs:139-142`). |
| Health | ✅ | `/health` y `/health/ready` con check de base de control. |
| Otras superficies | ✅ | `/auth/v1`, `/storage/v1`, `/functions/v1`, `/realtime/v1`, `/api/v1/**`. |

---

## 4. SDK `@domibase/client`

| Capacidad | Estado | Evidencia y observación |
| --- | --- | --- |
| `createClient`, `from` | ✅ | `client.ts:28-56`, validación de config/tabla. |
| `select`, `insert`, `update`, `delete` | ✅ | `query-builder.ts:104-159`; insert fila y bulk hasta 100 filas. |
| Filtros y `order`, `limit`, `range` | ✅ | `query-builder.ts:161-258`. |
| `single` / `maybeSingle` | ✅ | Semántica completa y probada en `single.test.js`. |
| Transporte HTTP | ✅ | `transport.ts`: GET/POST/PATCH/DELETE, envelope `{data,count}` / `{error}`. |
| `fetch` inyectable, `AbortSignal`, timeout | ✅ | `client.ts:73,96-115`; `transport.ts:239-282`. |
| Errores | ✅ | `errors.ts:6-60`, `{code,message,details?,hint?,status?}`. |
| SSR/Node/browser | ✅ con salvedad | Sin `window`/`document` en import; `realtime.ts` importa SignalR estáticamente. |
| Inmutabilidad del builder | ✅ | `query-builder.ts:391-407`. |
| Auth SDK | ✅ | `auth.ts`: signUp/signIn/signOut/refresh/onAuthStateChange, header Bearer. |
| Realtime SDK | ◐ | `realtime.ts`: SignalR, subscribe/unsubscribe; opciones no configurables desde `createClient`; tests solo de factory/validación. |
| Storage SDK | ✅ | `storage.ts`: upload/download/list/remove/createSignedUrl. |
| Functions SDK | ✅ | `functions.ts`: invoke con payload y validación de nombre. |
| Tests | ✅ como código | 70 casos visibles en 7 archivos; importan desde `dist`, por lo que requieren build previo. |

---

## 5. Ejemplo Astro

| Página | Estado | Flujo |
| --- | --- | --- |
| `/` | ✅ | CRUD de contactos vía formularios + PRG 303. |
| `/login` | ✅ demo | Registro/login, sesión en cookies `httpOnly`/`secure` en producción. |
| `/notas` | ✅ | Notas con RLS por `owner_id` derivado del JWT. |
| `/diagnostico` | ✅ | 7 comprobaciones en vivo: lectura, aislamiento, metadatos bloqueados, claves inválida/revocada/ausente. |
| `/tiempo-real` | ✅ demo | SignalR sobre `contactos` y `notas`. |
| `/almacen` | ✅ | Upload/list/remove/URL firmada. |
| `/funciones` | ✅ | Invocación de `demo-echo`. |

Configuración: `output:"server"`, `@astrojs/node` `mode:"standalone"`, puerto 4325, `checkOrigin:true`, variables `astro:env/server` con `access:"secret"`.

Observaciones de seguridad:

- `.env.example` contiene solo placeholders; `.env` real está gitignoreado.
- `/tiempo-real` inyecta deliberadamente la **clave pública** y la URL al bundle mediante `define:vars`; no es un secreto, pero queda expuesta en cliente.
- `tiempo-real.astro` construye parte del log con `innerHTML` desde campos del evento: riesgo potencial si el servidor enviara datos no saneados. Es hallazgo de lectura, no de ejecución.

---

## 6. Panel privado `frontend/admin`

| Capacidad | Estado | Evidencia |
| --- | --- | --- |
| Stack y arranque | ✅ | React 19, Vite 8, TS estricto, puerto 5190, proxy `/api` y `/admin`. |
| Sesión | ✅ | Login `POST /admin/v1/login` con usuario/contraseña del entorno; sesión `dbsess_` firmada, caducable y guardada solo en `sessionStorage`. |
| Proyectos | ✅ | Listar, crear, abrir, editar cupo, reintentar provisioning y eliminar con confirmación por slug. |
| Claves | ✅ | Emitir con valor único en memoria, listar, revocar y rotar desde **Conexión**. |
| Buckets y Funciones | retirado de UI | No aparecen en navegación; el código backend/shared se conserva. |
| Esquema | ✅ edición mínima | Table Editor con DDL tipado, columnas allowlisted, RLS `anon_select`/`anon_insert` y CRUD de filas. |
| Accesibilidad | ✅ | Pestañas con roles ARIA, tablas semánticas, `sr-only`, foco visible, reduced motion. |
| Responsive | ✅ | Media queries y navegación horizontal en móvil. |
| CSP | ◐ | `<meta>` inyectada en build; la cabecera HTTP `frame-ancestors 'none'` **no está aplicada en el despliegue**, aunque desde la resolución documental existe una plantilla Nginx de referencia en `admin-panel-plan.md`. |
| Tests de componentes/e2e en fuente | ⛔ | Solo hay 14 tests de `ControlClient`. |
| Creación de tablas/RLS desde panel | ⛔ | Diferido explícitamente. |

---

## 7. Pruebas

### 7.1 Backend — conteo estático

| Proyecto | Casos visibles |
| --- | ---: |
| `DomiBase.UnitTests` | 109 |
| `DomiBase.IntegrationTests` | 64 |
| `DomiBase.SecurityTests` | 9 |
| **Total** | **182** |

Cobertura visible: query builder, serialización, claves, JWT, Argon2, refresh tokens, Functions, health, Control Plane, CRUD Runtime, seguridad Runtime, RLS, Storage, Realtime, Schema Control y autenticación administrativa.

### 7.2 Panel

`frontend/admin/src/lib/control-client.test.ts`: **14 casos**, todos sobre `ControlClient` con `fetch` inyectable.

### 7.3 SDK

7 archivos, **70 casos** visibles: query builder, transporte, `single`, auth, storage, functions y realtime. Realtime tiene cobertura mínima.

### 7.4 Ejecución verificada en esta conversación

Estos resultados **no son deducidos de documentación**: se ejecutaron antes de esta auditoría, en el mismo entorno de trabajo.

- `dotnet build backend/DomiBase.sln`: **0 errores, 0 warnings**.
- `dotnet test backend/DomiBase.sln --no-build`: **182/182** —109 unitarios, 64 integración, 9 seguridad.
- `npm run check`: sin errores.
- `npm test`: **14/14**.
- `npm run build`: correcto.
- Verificación headless con CDP contra API real y PostgreSQL: pestaña Esquema activa, fila real `contactos`, un solo `h1`, cero errores de consola y sin desbordamiento en 1440×900 ni 390×844.

**No se reejecutaron** en esta auditoría los tests del SDK ni del ejemplo Astro; su estado “en verde” es evidencia histórica documental.

---

## 8. Operaciones y despliegue

| Elemento | Estado |
| --- | --- |
| PostgreSQL en Docker | ✅ `postgres:18-alpine`, `127.0.0.1:5433`, volumen y healthcheck. |
| Persistencia tras reinicio | ✅ verificada por `scripts/dev/phase-07-validate.mjs`. |
| Límites CPU/memoria | ⛔ pospuestos hasta medir, por decisión documentada. |
| Imagen Docker | 📄 preparada, no construida ni publicada. |
| Nginx / Proxy Manager | 📄 planificado, no implementado en repo. |
| Homelab | ⛔ no modificado; requiere inventario y autorización. |
| Backups/restauración | ✅ implementados en P1.6; volcado lógico por proyecto con verificación SHA-256 y restauración confirmada. Ver `p1-6-backups.md`. |
| Rotación/backup de secretos maestros | 📄 pendiente documental. |

---

## 9. Hallazgos de consistencia documental

| Hallazgo | Evidencia |
| --- | --- |
| `README.md:31` indica 175 tests backend y 11 del panel; la fuente tiene 182 y 14. | Desactualizado tras el inspector de esquema. |
| `roadmap.md` dice 11 tests en 6.6 y 14 en 6.7. | Contradicción interna; el dato correcto por fuente es 14. |
| `docs/development/local-setup.md` describe el panel sin pestaña Esquema. | Desactualizado. |
| `PRODUCT.md` describe el panel “sin tablas ni políticas” sin matizar que sí hay inspector de esquema y que las tablas existen en provisioning/demo. | Ambiguo/stale. |
| `docs/architecture/overview.md:78` marca el panel completado, pero `:79` aún lo lista como pendiente. | Contradicción residual. |
| `frontend/admin/README.md` y `admin-panel-plan.md` delegan `frame-ancestors 'none'` al proxy de producción. | No hay evidencia de esa cabecera en el repositorio. |
| Las cifras 98/50 en `overview.md:71,74,76` son instantáneas históricas. | Conviven con 182/14 sin etiquetarse claramente como históricas. |

### Resolución de los hallazgos de consistencia (2026-11-18)

- **Cifras globales:** `README.md` y los documentos vigentes reflejan 491 tests backend (311/134/46), 70 del SDK y 57 del panel. Las cifras 466/49 y 383/35 del informe base quedan solo como instantáneas históricas.
- **Roadmap 6.6/6.7:** 11 y 14 tests del cliente quedan etiquetados como instantáneas de cada fase; no se presentan como cifras contradictorias del mismo momento.
- **Panel en `local-setup.md`:** se enumeran Esquema, SQL de solo lectura, cupo, Backups y Auditoría, además de proyectos, claves, migraciones y extensiones.
- **`PRODUCT.md`:** ya no describe un panel “sin tablas ni políticas”; aclara que las tablas y políticas RLS se crean desde la Control Plane con DDL validado y que el inspector solo las lee.
- **`overview.md`:** el estado general marca P1.1–P1.7 como completados; no queda la contradicción entre “panel completado” y “panel pendiente”.
- **`frame-ancestors`:** `admin-panel-plan.md` incluye la plantilla Nginx `add_header Content-Security-Policy "frame-ancestors 'none';" always;` y `frontend/admin/README.md` aclara que es configuración de despliegue pendiente de aplicación, no una cabecera ya activa. No se tocó el homelab.
- **Instantáneas 98/50 y 182/14:** `overview.md` las identifica como cifras de las fases 5 y 7 y remite al estado general para el total vigente.

---

## 10. Conclusión

DomiBase tiene una implementación sustancial y verificable de su núcleo: arquitectura modular, Control/Runtime separados, provisioning con aislamiento por base, REST con RLS y allowlists, auth propia, realtime, storage, functions, SDK, ejemplo Astro y panel administrativo con inspector de esquema, migraciones, cupos y backups.

No debe describirse como equivalente a una plataforma completa tipo Supabase: faltan capacidades concretas —configuración genérica de proyectos, recuperación/verificación/OAuth/MFA, paginación total, PgBouncer y despliegue— y varias que existen son parciales. La gestión de proyectos con borrado, la rotación de claves, el DDL administrativo y los backups/restauración se resolvieron en P0.1/P0.3/P1.1/P1.6; este informe separa deliberadamente **código real**, **evidencia de ejecución** y **afirmaciones documentales** para que la comparación posterior no herede suposiciones.
