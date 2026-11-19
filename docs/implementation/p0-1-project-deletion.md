# P0.1 — Eliminación segura de proyectos

**Estado:** completado y verificado (2026-11-17).  
**Dependencia:** reutiliza `DeprovisionAsync` y el guard de nombres de P0.2.

## 1. Funcionalidades implementadas

- Nuevo `DELETE /api/v1/projects/{id}` privado, protegido por el token administrativo igual que el resto del Control Plane.
- Confirmación administrativa por defecto:
  - cabecera `X-DomiBase-Confirm-Delete: delete` (case-insensitive), y
  - cuerpo `{ "confirmSlug": "<slug exacto>" }`.
  - Sin estas dos señales la operación devuelve `400 Validation` y **no** toca ningún recurso.
- Idempotencia: repetir la misma petición tras un borrado completado devuelve `204 No Content`. Si una ejecución anterior quedó a medias, la repetición reanuda la limpieza.
- Ciclo de vida observable: el proyecto pasa a `Deleting`, se registran `project.delete_requested`, `api_key.revoke` por clave y `project.deleted` (o `project.delete_failed`) en auditoría.
- Revocación de todas las API keys antes de desprovisionar: las credenciales públicas dejan de resolver inmediatamente aunque fallara la limpieza PostgreSQL.
- Limpieza completa:
  - objetos y buckets de Storage (backend + metadatos),
  - funciones WebAssembly, claves de API, claves de firma de Auth, operaciones de provisioning y la fila del proyecto en `domibase_control`,
  - base de datos y los cuatro roles dedicados en PostgreSQL mediante `DeprovisionAsync`.
- Las credenciales de runtime cifradas se eliminan del metadato antes de borrar la base.
- Si la limpieza falla, el proyecto permanece en `Deleting` con `failure_reason` y se audita `project.delete_failed`; el reintento es seguro y no expone SQL ni secretos.

## 2. Arquitectura modificada

- `ProjectService.DeleteAsync` orquesta confirmación → estado `Deleting` → Storage → revocación → desprovisioning → borrado de metadatos → auditoría.
- `IProjectRepository.DeleteProjectMetadataAsync` elimina explícitamente las tablas sin FK a `projects` (storage y functions) antes de las que sí la tienen. Las entradas de auditoría se conservan: están keyed por `target_id` y no tienen FK al proyecto.
- `ControlEndpoints` usa `[FromBody]` explícito: ASP.NET Core no infiere cuerpos en `MapDelete` (solo POST/PUT/PATCH). Sin esa anotación el host no arranca.
- El sink de P0.2 se reutiliza para las operaciones `database_cleanup` y `role_cleanup`, con el mismo `Attempt` y `CorrelationId` por petición.

## 3. Archivos creados

- `backend/tests/DomiBase.IntegrationTests/ProjectDeletionTests.cs`
- `docs/implementation/p0-1-project-deletion.md`

## 4. Archivos modificados

- `backend/src/DomiBase.Application/Projects/IProjectRepository.cs`
- `backend/src/DomiBase.Application/Projects/ProjectService.cs`
- `backend/src/DomiBase.Api/Contracts/ProjectContracts.cs`
- `backend/src/DomiBase.Api/Endpoints/ControlEndpoints.cs`
- `backend/src/DomiBase.Infrastructure/ControlPlane/EfProjectRepository.cs`
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs`
- `docs/implementation/roadmap.md`
- `README.md`
- `docs/architecture/overview.md`

## 5. Migraciones creadas

Ninguna. El borrado usa las tablas y relaciones ya presentes en el modelo EF; no se añaden columnas ni restricciones.

## 6. Pruebas ejecutadas

- `dotnet build backend/DomiBase.sln`: 0 errores, 0 warnings.
- `dotnet test backend/DomiBase.sln`: **197/197** en verde.
  - UnitTests: 116.
  - IntegrationTests: 71 (2 nuevos de borrado).
  - SecurityTests: 10 (1 nuevo: `DELETE` sin token administrativo → `401 UNAUTHORIZED`).

Casos nuevos relevantes: borrado con confirmación válida (recursos PostgreSQL y metadatos eliminados, otro proyecto intacto, auditoría conservada, repetición idempotente) y rechazo sin cabecera, con slug incorrecto o sin slug.

## 7. Resultados

P0.1 cumple el criterio acordado: borrado solo del propietario autenticado, confirmación explícita, aislamiento por proyecto, revocación inmediata de claves, limpieza idempotente y recuperable, y auditoría de la operación.

## 8. Riesgos identificados

- `DELETE` con cuerpo es menos estándar que un `POST /projects/{id}:delete`; se eligió para respetar el contrato REST acordado y se compensa con la anotación `[FromBody]` y la confirmación doble.
- La limpieza de Storage elimina objetos del backend antes de borrar metadatos; si falla un objeto concreto, la excepción deja el proyecto en `Deleting` y el reintento continúa.
- `DeleteProjectMetadataAsync` ejecuta varías sentencias `ExecuteDeleteAsync` fuera de una transacción explícita de aplicación. Un fallo a mitad de camino deja metadatos parciales; el reintento es idempotente pero conviene envolverlo en transacción en una iteración posterior.
- La auditoría se conserva deliberadamente, por lo que `target_id` de un proyecto borrado sigue visible para el propietario.

## 9. Limitaciones pendientes

- No hay "papelera" ni restauración: el borrado es irreversible.
- P0.3 (rotación/expiración de keys) y P0.4 (keyring de secretos) ya están completadas; ver `p0-3-api-keys.md` y `p0-4-secrets.md`.
- Las claves secretas aún no son emisibles; por tanto no hay secretos que revocar en este borrado más allá de las claves públicas.

## 10. Siguiente fase

**P0.3 — API Keys**: claves secretas `db_secret_`, rotación con convivencia de la clave anterior, expiración opcional y auditoría, sin elevar privilegios en el Runtime.
