# Fase 3 — Control Plane y provisioning — completada

## Objetivo
Convertir el contrato placeholder `/api/v1` en un Control Plane real sobre `domibase_control`: migración EF Core, proyectos, API keys públicas con hash, provisioning idempotente de la base y roles de cada proyecto, y auditoría. El Runtime continúa en 501 hasta la Fase 4.

## Entregado

### Migración EF Core
- Herramienta local `dotnet-ef` **10.0.15** fijada en `dotnet-tools.json` (la global 8.0.11 no es compatible con EF Core 10).
- `Microsoft.EntityFrameworkCore.Design` añadido al proyecto `DomiBase.Api` para el design-time.
- Migración `20261117013058_InitialControlPlane` aplicada a `domibase_control`.
- Tablas creadas: `projects`, `api_keys`, `provisioning_operations`, `audit_entries` y `__EFMigrationsHistory`.
- **No hay auto-migración al arrancar.** La migración se aplica explícitamente:

```powershell
dotnet tool restore
dotnet ef database update --project backend/src/DomiBase.Infrastructure --startup-project backend/src/DomiBase.Api
```

### Application
- `ProjectService` orquesta proyecto, provisioning, claves y auditoría.
- `ControlResult<T>` mantiene los códigos HTTP fuera de la capa de aplicación (`Validation`, `NotFound`, `Conflict`).
- `IApiKeyGenerator` / `ApiKeyGenerator` generan claves criptográficas y hashes verificables.
- Validación de entrada: slug normalizado (minúsculas, espacios/guiones bajos a guiones), nombre máximo 120 caracteres, etiqueta máxima 120 caracteres.
- Los fallos de provisioning **no** se devuelven como error HTTP: el proyecto se crea con `status: Failed`, `failureReason` y puede reintentarse.

### Infraestructura
- `EfProjectRepository` persiste metadatos mediante EF Core sobre `domibase_control`.
- Las violaciones de unicidad (`PostgresErrorCodes.UniqueViolation`) se traducen a conflicto controlado y la entidad se desvincula del contexto.
- Auditar nunca expone hashes ni secretos. Listar claves devuelve solo metadatos (`prefix`, no el valor ni el hash).

### Provisioning
Flujo síncrono, idempotente y reintentable, con un advisory lock por base de datos (`pg_try_advisory_lock(hashtext(database))`):

1. **`roles`** — crea (si no existen) los roles del proyecto como `NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`.
2. **`database`** — crea `domibase_<slug>` (guiones → guiones bajos) con `OWNER` el rol owner, `ENCODING 'UTF8'` y `TEMPLATE template0`. `CREATE DATABASE` se ejecuta fuera de transacción. Si la base ya existe con otro owner, el provisioning se niega a modificarla.
3. **`database_permissions`** — `REVOKE ALL ON DATABASE ... FROM PUBLIC`, `GRANT CONNECT` a runtime/anon, `REVOKE CREATE ON SCHEMA public FROM PUBLIC` y `GRANT USAGE ON SCHEMA public` a runtime/anon.

Cada paso verifica el estado actual de PostgreSQL antes de actuar, por lo que repetir el provisioning no duplica recursos. Cada paso queda registrado en `provisioning_operations`.

Una vez creada la fila del proyecto, el provisioning **no se cancela** si el cliente HTTP se desconecta: se completa hasta un estado terminal (`Ready`/`Failed`). Cualquier excepción inesperada se registra y termina en `Failed`, habilitando el reintento.

### Endpoints reales
Todos bajo `/api/v1` y protegidos por el token administrativo.

| Método | Ruta | Comportamiento |
|---|---|---|
| GET | `/api/v1/projects` | Lista proyectos ordenados por creación descendente. |
| GET | `/api/v1/projects/{id}` | Consulta un proyecto; 404 si no existe. |
| POST | `/api/v1/projects` | Valida, crea metadatos, provisiona y devuelve **201**. `status` es `Ready` o `Failed`. |
| POST | `/api/v1/projects/{id}/provision` | Reintenta el provisioning de un proyecto `Failed`; 409 si está `Provisioning`/`Deleting`; 200 si ya está `Ready`. |
| GET | `/api/v1/projects/{id}/keys` | Lista claves del proyecto (sin valor ni hash). |
| POST | `/api/v1/projects/{id}/keys` | Crea una clave pública; devuelve el valor completo **una sola vez** con **201**. Solo proyectos `Ready`. |
| DELETE | `/api/v1/projects/{id}/keys/{keyId}` | Revoca la clave; **204** idempotente (una segunda llamada también devuelve 204). |

`/provision` y `GET .../keys` se añadieron a la lista original del contrato porque el diseño idempotente y la administración de claves los requieren. Se documentan como rutas administrativas.

### API keys
- Valor: `db_public_` + 32 bytes aleatorios (`RandomNumberGenerator`) en base64url (53 caracteres en total).
- Prefijo mostrado/almacenado: `db_public_` + 8 caracteres del valor aleatorio (18 caracteres).
- Hash almacenado: `HMAC-SHA-256(pepper, valor)` en hexadecimal (64 caracteres), único. **Nunca se guarda el valor original.**
- `DOMIBASE_API_KEY_PEPPER` es obligatoria al arrancar; mínimo 16 caracteres; `ApiKeyGenerator` también rechaza peppers débiles.
- `db_secret_` está reservado: crear una clave `secret` devuelve **400**.
- Revocación idempotente. No hay endpoint de rotación todavía: se rota revocando y creando una nueva clave.
- La clave pública solo identifica el proyecto; el Runtime la validará y aplicará RLS en la Fase 4.

### Seguridad y errores
- Middleware administrativo para todo `/api/v1`; comparación en tiempo constante (SHA-256 + `FixedTimeEquals`).
- Cuerpos JSON inválidos o ausentes devuelven **400 `VALIDATION_ERROR`** (se captura `BadHttpRequestException`).
- Sobre de error uniforme `{ "error": { "code", "message", "details"?, "hint"? } }`; sin stack traces, SQL ni credenciales.
- Auditoría persistida: `project.create`, `project.provisioned`, `project.provision_failed`, `api_key.create`, `api_key.revoke`.
- El proceso de provisioning usa la conexión administrativa; nunca se expone al cliente ni al Runtime.

## Verificación ejecutada

### Build
```
dotnet build backend/DomiBase.sln
Build succeeded. 0 Warning(s), 0 Error(s)
```

### Tests
```
dotnet test backend/DomiBase.sln
DomiBase.UnitTests        49 passed
DomiBase.IntegrationTests  7 passed
DomiBase.SecurityTests     6 passed
Total                     62 passed, 0 failed
```

- **Unitarios nuevos:** prefijo y entropía de claves, unicidad, hash determinista, dependencia del pepper y rechazo de peppers débiles.
- **Integración (PostgreSQL real):** ciclo completo proyecto → DB/roles → clave → listado sin filtrar valor/hash → revocación doble → reintento de provisioning; validación; cuerpo ausente; proyecto inexistente; duplicado. Los tests crean y eliminan bases/roles `it-*` y verifican que no quedan artefactos.
- **Seguridad:** token ausente/incorrecto y rutas Runtime sin autenticación administrativa; token válido alcanza la capa de aplicación (401 vs 400).
- La paralelización de la asamblea de integración está deshabilitada porque comparte variables de entorno y provisioning real.

### Ejecución real (Windows + PostgreSQL en Docker)
`dotnet run --project backend/src/DomiBase.Api` → `http://localhost:5080`:

| Comprobación | Resultado |
|---|---|
| `GET /health` | 200 |
| `GET /health/ready` | 200 contra PostgreSQL real |
| `GET /api/v1/projects` sin token | 401 |
| `POST /api/v1/projects` (`paneles-solares`) | 201 `Ready`, `domibase_paneles_solares` |
| Base y roles creados | DB + `_owner`, `_runtime`, `_anon` (NOLOGIN, sin superuser/BYPASSRLS) |
| `POST .../keys` | 201 `db_public_...` (53 chars) |
| `GET .../keys` | 200, sin valor completo ni `secretHash` |
| `DELETE .../keys/{id}` dos veces | 204 y 204 |
| `POST .../provision` en `Ready` | 200 `Ready` |
| Crear clave `secret` | 400 |
| Crear proyecto duplicado | 409 |
| `GET /rest/v1/contactos` | 501 (Runtime pendiente de Fase 4) |

Comprobaciones en PostgreSQL: hash de 64 caracteres almacenado, clave revocada, tres pasos de provisioning en `Succeeded`, auditoría con las cinco acciones. El proyecto de smoke y sus roles/base se eliminaron al terminar; `domibase_control` quedó sin filas de proyecto y sin artefactos `it-*`. La base `domibase_smoke_check` de Fase 1 se conserva intencionalmente.

## Limitaciones conocidas
- El provisioning es síncrono dentro de la petición HTTP (comandos con timeout de 30 s). No hay cola/worker todavía.
- Un proyecto atascado en `Provisioning` por un fallo del proceso no se puede reintentar mientras conserve ese estado (el endpoint devuelve 409); requiere intervención administrativa. El caso normal de fallo termina en `Failed` y sí es reintentable.
- Los roles `_runtime`/`_anon` son `NOLOGIN`: la estrategia de login técnico/conexión del Runtime se define en la Fase 4.
- No hay endpoint de borrado de proyecto (`Deleting` está reservado) ni de rotación de claves.
- No hay rate limiting ni límites de payload en el Control Plane (fases posteriores).
- El rol administrativo local de PostgreSQL es superuser. Antes de desplegar debe reducirse a los privilegios mínimos necesarios (`CREATEDB`, `CREATEROLE` y acceso a `domibase_control`).
- La rotación del pepper invalidaría todos los hashes existentes; no hay estrategia de rotación de pepper todavía.

## Siguiente fase
**Fase 4 — Runtime Plane:** resolución de proyecto por API key, verificación de hash/estado, CRUD `/rest/v1/{table}`, allowlists de identificadores, SQL parametrizado, transacciones con `SET LOCAL ROLE`/`set_config`, RLS y pruebas de aislamiento entre proyectos y pooling.
