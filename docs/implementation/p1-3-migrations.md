# P1.3 — Migraciones versionadas por proyecto

**Fecha:** 2026-11-17  
**Estado:** completado y verificado.  
**Objetivo:** que el propietario pueda aplicar un historial de migraciones versionado e inmutable por proyecto desde la Control API, reutilizando el DDL validado de P1.1, sin SQL arbitrario y con idempotencia por versión+contenido.

## 1. Funcionalidades implementadas

Operaciones bajo `/api/v1/projects/{projectId}/migrations` (Control Plane, protegidas por el token administrativo):

| Método | Ruta | Acción |
| --- | --- | --- |
| `GET` | `` | Listar el historial de migraciones aplicadas, ordenado por versión. |
| `POST` | `` | Aplicar una migración: lote tipado de operaciones DDL con versión y nombre inmutables. |

El cuerpo de `POST` es un plan tipado, nunca SQL:

```json
{
  "version": 1,
  "name": "initial_schema",
  "operations": [
    { "type": "create_table", "name": "contactos", "columns": [ ... ] },
    { "type": "create_policy", "table": "contactos", "name": "contactos_select", "commands": ["SELECT"], "roles": ["anon"], "using": "true", "withCheck": "true" }
  ]
}
```

Tipos de operación soportados (los mismos que P1.1): `create_table`, `drop_table`, `add_column`, `alter_column`, `drop_column`, `create_index`, `drop_index`, `create_policy`, `alter_policy`, `drop_policy`. El parser (`SchemaRequestParser`) y el validador (`SchemaDdlValidator`) son los de P1.1, sin duplicar reglas.

**Decisiones de diseño:**

- **Historial privado por base:** la tabla `domibase_migrations.applied` vive dentro de cada base de proyecto. El esquema `domibase_migrations` no es visible para el inspector de esquema (fijado a `public`) ni para el Runtime (allowlists derivadas de `public` y privilegios).
- **Inmutabilidad:** la versión y el nombre se validan como identificadores seguros. El checksum es `v1.<SHA-256>` determinista calculado sobre las operaciones concretas serializadas por tipo concreto. Reaplicar la misma versión con distinto nombre o checksum se rechaza con `409 CONFLICT`.
- **Orden:** las versiones deben aplicarse en orden creciente. Una versión menor que la máxima ya aplicada se rechaza con `409 CONFLICT` sin ejecutar DDL.
- **Idempotencia:** reaplicar una versión idéntica devuelve `200` con `alreadyApplied: true` y no ejecuta DDL ni escribe otra fila.
- **Serialización:** `pg_advisory_xact_lock(hashtext(@database))` dentro de la transacción serializa migraciones concurrentes de la misma base.
- **Atomicidad:** DDL y fila de historial se confirman en una sola transacción. Cualquier error revierte ambas. No se ejecuta `CREATE DATABASE` ni se toca `domibase_control`.
- **Errores:** los `PostgresException` se traducen a mensajes seguros sin SQL interno; el SQLSTATE solo se conserva como `hint`. La validación previa al plano (`400`) se distingue del conflicto de estado (`409`).
- **Auditoría:** `migration.applied`, `migration.reapplied` y `migration.list` en `audit_entries`, sin SQL ni secretos.

## 2. Arquitectura modificada

- **`DomiBase.Application.Schema`**: añade `MigrationPlan`, `AppliedMigration`, `MigrationSummary`, `MigrationApplyOutcome`, `MigrationChecksum`, `IMigrationStore`, `ProjectSchemaResolver` y `MigrationService`. La capa Application sigue sin referenciar Npgsql.
- **`DomiBase.Provisioning.Admin`**: `ProjectMigrationStore` implementa `IMigrationStore` con la conexión privilegiada, el advisory lock, la tabla de historial y el DDL real. Reutiliza los helpers de `ProjectSchemaAdmin` (hechos `internal`): generación de sentencias, verificación de índice, ejecución y traducción de errores. No duplica lógica DDL.
- **`DomiBase.Api`**: `MigrationControlEndpoints` convierte el JSON discriminado en operaciones tipadas usando `SchemaRequestParser`, compartido con P1.1. `SchemaAdminService` usa el mismo `ProjectSchemaResolver` que `MigrationService`, eliminando la duplicación del guard de proyecto `Ready`.
- **Separación de planos:** ninguna ruta nueva está bajo `/rest/v1`. El middleware administrativo existente protege `/api/v1`; las claves públicas no tienen efecto.

## 3. Archivos creados

**Backend — Application**
- `backend/src/DomiBase.Application/Schema/MigrationPlan.cs`
- `backend/src/DomiBase.Application/Schema/MigrationChecksum.cs`
- `backend/src/DomiBase.Application/Schema/IMigrationStore.cs`
- `backend/src/DomiBase.Application/Schema/ProjectSchemaResolver.cs`
- `backend/src/DomiBase.Application/Schema/MigrationService.cs`

**Backend — Provisioning**
- `backend/src/DomiBase.Provisioning/Admin/ProjectMigrationStore.cs`

**Backend — API**
- `backend/src/DomiBase.Api/Endpoints/MigrationControlEndpoints.cs`
- `backend/src/DomiBase.Api/Endpoints/SchemaRequestParser.cs`

**Tests**
- `backend/tests/DomiBase.UnitTests/MigrationPlanTests.cs`
- `backend/tests/DomiBase.IntegrationTests/MigrationTests.cs`

**Documentación**
- `docs/implementation/p1-3-migrations.md`
- `frontend/admin/src/lib/migration-plan.ts`
- `frontend/admin/src/lib/migration-plan.test.ts`
- `frontend/admin/src/components/MigrationSection.tsx`

## 4. Archivos modificados

- `backend/src/DomiBase.Application/Schema/SchemaAdminService.cs` — usa `ProjectSchemaResolver` compartido.
- `backend/src/DomiBase.Provisioning/Admin/ProjectSchemaAdmin.cs` — helpers `internal` para reutilización dentro del módulo.
- `backend/src/DomiBase.Provisioning/ProvisioningServiceCollectionExtensions.cs` — registro de `IMigrationStore`.
- `backend/src/DomiBase.Api/ControlPlane/ControlPlaneServiceCollectionExtensions.cs` — registro de `ProjectSchemaResolver` y `MigrationService`.
- `backend/src/DomiBase.Api/Endpoints/SchemaControlEndpoints.cs` — usa el parser compartido.
- `backend/src/DomiBase.Api/Program.cs` — mapeo de `MapMigrationControlEndpoints()`.
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs` — 3 tests de autenticación de rutas de migraciones.
- `README.md`, `docs/implementation/roadmap.md`, `docs/architecture/overview.md`, `docs/architecture/security.md`, `docs/audits/DOMIBASE_TECHNICAL_AUDIT.md` — estado y contratos actualizados.

## 5. Migraciones creadas

**Ninguna migración EF.** El historial de P1.3 no es una migración de `domibase_control`: se crea perezosamente como `domibase_migrations.applied` dentro de cada base de proyecto con DDL privilegiado y transaccional. La auditoría reutiliza `audit_entries`.

## 6. Pruebas ejecutadas

```powershell
dotnet build backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
dotnet test  backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
```

Nota de entorno: el proceso manual `DomiBase.Api` bloquea los DLL de `bin\Debug\net10.0`, por lo que se usa `-p:BaseOutputPath=bin-p11\` para no interrumpirlo. El resultado funcional es idéntico.

| Suite | Antes de P1.3 | Después de P1.3 |
| --- | ---: | ---: |
| UnitTests | 169 | **180** |
| IntegrationTests | 83 | **88** |
| SecurityTests | 14 | **17** |
| **Total backend** | **266** | **285** |

Casos nuevos relevantes:

- Aplicar migración con `create_table` + política → tabla con RLS, política real y una fila de historial con checksum `v1.`.
- Reaplicar versión idéntica → `alreadyApplied: true`, historial sigue con una sola fila.
- Misma versión con otro nombre o distintas operaciones → `409 CONFLICT`.
- Versión menor que la máxima aplicada → `409 CONFLICT` sin DDL.
- Nombre inseguro, versión fuera de rango, operación inválida o inyección en identificador → `400 VALIDATION_ERROR` sin tabla centinela ni fila de historial.
- Historial invisible para el inspector (`public`) y para el Runtime (`/rest/v1/applied` no responde `200`).
- Historial aislado: aplicar en un proyecto no crea la tabla ni el historial en otro.
- Rutas `/migrations` sin token, con token erróneo y con solo `apikey` público → `401`.

Cada test de integración aprovisiona su propio proyecto (base propia) y lo elimina al final, de modo que el orden de ejecución no afecta a la validación de versiones crecientes.

## 7. Resultados

- Build limpio: **0 errores, 0 warnings**.
- Suite completa: **285/285 tests en verde**.
- PostgreSQL 18.7 en Docker (`127.0.0.1:5433`) sigue healthy; no se detuvieron ni modificaron contenedores.
- Ninguna credencial, SQL interno ni nombre de rol físico aparece en respuestas de error.
- La API manual en ejecución no fue interrumpida.

## 8. Riesgos identificados

- **DDL privilegiado reutilizado:** las migraciones ejecutan sentencias como rol owner. Mitigado con el mismo validador de P1.1, allowlists y ausencia de SQL arbitrario. Cualquier operación nueva debe pasar por `SchemaDdlValidator`.
- **Historial conviviendo con datos:** la tabla de historial está en un esquema aparte y no es expuesta por el inspector ni el Runtime. Un cambio futuro en el inspector debe mantener el filtro `public`.
- **Concurrencia:** el advisory lock serializa por base; el candado de catálogo de PostgreSQL sigue gestionando objetos individuales.
- **Sin rollback automático de versiones:** no hay comando para desaplicar una migración. La reversión se modela como una nueva versión con la operación inversa, preservando la inmutabilidad del historial.

## 9. Limitaciones pendientes (fuera de P1.3)

- No hay dependencias entre migraciones ni grafo de aplicación: solo orden por versión.
- No hay migraciones automáticas al arrancar ni descubrimiento desde archivos: el propietario las aplica explícitamente vía API.
- Catálogo de extensiones resuelto en P1.5 (`p1-5-extensions.md`): allowlist de diez extensiones `trusted`, listado en vivo y DDL idempotente desde la Control API y el panel.
- El constructor del panel no ofrece `add_column`/`alter_column` por la colisión del campo `type` en `SchemaRequestParser`; esas dos operaciones se envían desde la Control API sin cambiar el contrato.

## 10. Panel de migraciones (extensión posterior)

La pestaña **Migraciones** de `frontend/admin` consume el contrato de esta entrega sin modificarlo. `ControlClient` añade `listMigrations`/`applyMigration`; `migration-plan.ts` convierte drafts en planes tipados y valida localmente los identificadores, tipos, literales, índices y plantillas de política permitidas por `SchemaDdlValidator`. La vista muestra el historial, una vista previa JSON y confirma la aplicación con el `<dialog>` nativo ya usado por Claves y Extensiones. No hay editor de SQL y ninguna ruta nueva vive bajo `/rest/v1`.

Cifra acumulada tras esta extensión: **367 tests backend** y **34 tests del panel** (22 preexistentes, 3 del cliente y 9 del planificador); build de producción del panel correcto. Estado al cerrar P1.4: **383 tests backend** y **35 tests del panel** (instantánea histórica; el total vigente está en `docs/implementation/roadmap.md`). Detalle en `docs/implementation/admin-panel-plan.md` y `docs/implementation/p1-4-rate-limits.md`.

Verificación E2E real: `node scripts/dev/verify-migrations-panel.mjs` conduce Chrome headless contra la API y el panel locales, crea un proyecto temporal `mig-e2e-*`, aplica un plan `create_table` + `create_policy` desde la UI, comprueba el historial (checksum `v1.…` y 2 operaciones), la idempotencia (`alreadyApplied`), el conflicto sin nueva fila, la tabla semántica, `:focus-visible`, la ausencia de desbordamiento a 390×844 y una consola/respuestas sin errores, y elimina el proyecto al terminar. Resultado: **15/15 comprobaciones en verde**. Capturas: `.impeccable/review/migrations-desktop.png` y `.impeccable/review/migrations-mobile.png`. La verificación detectó y corrigió el sizing intrínseco del planner en móvil mediante pistas `minmax(0, 1fr)` y `flex-wrap` en `.field__row`, sin recurrir a `overflow-x: hidden`.

## 11. Siguiente fase

P1.2 quedó completado y verificado después de esta entrega: la consola de consulta acotada está en el panel y su Control API reutiliza la conexión privilegiada. Ver `docs/implementation/p1-2-sql-editor.md`. Las restantes entregas P1–P10 se planificarán individualmente.
