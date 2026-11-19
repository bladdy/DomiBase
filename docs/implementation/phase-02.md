# Fase 2 — Backend base (Windows) — completada

## Objetivo
Crear la base del backend ASP.NET Core 10 ejecutándose **directamente en Windows** contra PostgreSQL 18 en Docker: solución por capas, configuración, health checks, OpenAPI, autenticación administrativa y esqueletos honestos de Control/Runtime.

## Entregado

### Solución
`backend/DomiBase.sln` (formato clásico `.sln`) con:

| Proyecto | Responsabilidad |
|---|---|
| `DomiBase.Api` | Host único. Endpoints `/health`, `/health/ready`, `/api/v1` (Control) y `/rest/v1` (Runtime). |
| `DomiBase.Domain` | Entidades, enums, validación/nomenclatura de slugs. Sin dependencias de infraestructura. |
| `DomiBase.Application` | Seguridad de aplicación: opciones y validación del token administrativo. |
| `DomiBase.Infrastructure` | Configuración `.env`, EF Core/Npgsql del plano de control, health check y DI. |
| `DomiBase.Provisioning` | Fábrica de conexiones privilegiadas para provisioning (no usada por Runtime). |
| `DomiBase.UnitTests` | Tests puros (slug, token administrativo, parser `.env`). |
| `DomiBase.IntegrationTests` | Arranque real del host con `WebApplicationFactory`. |
| `DomiBase.SecurityTests` | Autenticación del Control Plane y aislamiento de rutas Runtime. |

`Infrastructure` y `Provisioning` usan `FrameworkReference Microsoft.AspNetCore.App` porque solo son consumidos por el host único y necesitan configuración/DI/health checks. No se publican de forma independiente.

### Configuración
- Carga opcional de `.env` desde el directorio de contenido y sus padres (máximo 8 niveles).
- **Prioridad:** variables de entorno / appsettings reales > `.env`. El `.env` solo rellena claves ausentes.
- Claves obligatorias al arrancar (fail-fast):
  - `CONTROL_DB_CONNECTION_STRING` (metadatos, EF Core).
  - `PROVISIONING_DB_CONNECTION_STRING` (conexión privilegiada; solo provisioning).
  - `DOMIBASE_ADMIN_TOKEN` (mínimo 16 caracteres).
- Clave opcional: `DOMIBASE_CORS_ORIGINS` (lista separada por comas). En Development, si no está definida, se permite `http://localhost:4321` (Astro). CORS es un control de navegador, **no** autorización.

### Modelo de datos (EF Core, sin migración todavía)
Entidades mapeadas en `DomiBaseDbContext` para `domibase_control`, en snake_case:

- `projects` — id, slug único, name, status (`Provisioning|Ready|Failed|Deleting`), database_name único, failure_reason, created_at, updated_at.
- `api_keys` — id, project_id, kind (`Public|Secret`), prefix, secret_hash único (nunca el valor original), label, created_at, revoked_at, last_used_at.
- `provisioning_operations` — id, project_id, step, status, attempt, error, correlation_id, started_at, completed_at.
- `audit_entries` — id, occurred_at, actor, action, target_type, target_id, details (jsonb), correlation_id.

`ProjectNaming` deriva nombres deterministas y seguros (≤63 bytes):
`domibase_<slug>_owner`, `domibase_<slug>_runtime`, `domibase_<slug>_anon`. `MaxSlugLength = 40` para que el rol más largo quepa en el límite de PostgreSQL.

**Pendiente para Fase 3:** generar y aplicar la migración inicial (`dotnet ef migrations add`). En Fase 2 el modelo existe pero no se ha creado ninguna tabla.

### Endpoints
- `GET /health` — liveness, 200 con identidad del servicio.
- `GET /health/ready` — readiness; comprueba PostgreSQL del plano de control con timeout de 5 s; 503 si falla y sin exponer detalles internos.
- Control (`/api/v1`, protegido): listar/crear/consultar proyectos y crear/revocar claves. Responden **501** con sobre de error hasta Fase 3.
- Runtime (`/rest/v1/{table}`: GET/POST/PATCH/DELETE). Responden **501** hasta Fase 4.
- OpenAPI en `/openapi/v1.json` (solo Development).

### Seguridad
- Autenticación administrativa mediante `X-DomiBase-Admin-Token` (también se acepta `Authorization: Bearer`).
- Comparación en **tiempo constante**: se hashean ambos valores con SHA-256 y se usa `CryptographicOperations.FixedTimeEquals`.
- El middleware protege todo `/api/v1`; nunca se aplica a `/rest/v1` ni a health checks.
- La API **no arranca** sin token administrativo válido (evita un Control Plane silenciosamente abierto).
- Errores normalizados `{ "error": { "code", "message", "details"?, "hint"? } }`; el middleware de excepciones no filtra stack traces ni SQL.
- Runtime no expone rutas administrativas: una petición a `/rest/v1/projects` se trata solo como nombre de tabla placeholder.

## Verificación ejecutada

### Build
```
dotnet build backend/DomiBase.sln
Build succeeded. 0 Warning(s), 0 Error(s)
```
Se alinearon `Microsoft.EntityFrameworkCore` y `Microsoft.EntityFrameworkCore.Relational` a `10.0.15` para eliminar el conflicto con `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`.

### Tests
```
dotnet test backend/DomiBase.sln
DomiBase.UnitTests        42 passed
DomiBase.IntegrationTests  1 passed
DomiBase.SecurityTests     6 passed
Total                     49 passed, 0 failed
```

### Ejecución real (Windows + PostgreSQL en Docker)
`dotnet run --project backend/src/DomiBase.Api` → `http://localhost:5080`:

| Comprobación | Resultado |
|---|---|
| `GET /health` | 200 `{"status":"ok","service":"domibase-api",...}` |
| `GET /health/ready` | 200 `{"status":"ready","checks":{"control-database":{"status":"healthy"}}}` |
| `GET /api/v1/projects` sin token | 401 `UNAUTHORIZED` |
| `GET /api/v1/projects` token incorrecto | 401 `UNAUTHORIZED` |
| `POST /api/v1/projects` token válido | 501 `NOT_IMPLEMENTED` |
| `GET /rest/v1/contactos` | 501 `NOT_IMPLEMENTED` |
| `GET /openapi/v1.json` | 200, 6 rutas publicadas |

## Limitaciones conocidas
- No hay migraciones ni tablas en `domibase_control` todavía (Fase 3).
- Control y Runtime son contratos placeholder; no se simula seguridad ni datos.
- Sin rate limiting, límites de payload ni auditoría persistida todavía (fases posteriores).
- OpenAPI solo en Development.
- El pool/conexión de Runtime y RLS se implementan en Fase 4.
- El health check de readiness requiere PostgreSQL en ejecución; por eso los tests automatizados actuales no lo consultan (se verificó manualmente contra el contenedor real).

## Siguiente fase
**Fase 3 — Control Plane:** migración inicial de `domibase_control`, endpoints reales de proyectos y API keys, hash HMAC-SHA-256 con pepper, provisioning idempotente/recuperable y auditoría.
