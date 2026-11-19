# P0.2 — Provisioning observable y compensable

**Estado:** completado y verificado (2026-11-17).  
**Antecedente:** `docs/implementation/technical-audit.md` identificó que `ProvisioningStatus.Compensated` existía solo como enumeración y que los pasos se persistían en bloque después del outcome.

## 1. Funcionalidades implementadas

- Cada paso privilegiado se registra en `domibase_control.provisioning_operations` como `Running` **antes** de ejecutar SQL y con su estado terminal (`Succeeded` / `Failed`) justo después.
- Cada Ejecución tiene un `Attempt` creciente y un `CorrelationId` compartido por todos sus pasos.
- Compensación real: tras un fallo con recursos creados, se inspecciona el estado real de PostgreSQL y se eliminan base de datos y roles en orden inverso. Los pasos de limpieza se registran como `Compensated`.
- Desprovisioning idempotente (`DeprovisionAsync`), base reutilizable por P0.1.
- Verificación de propiedad: solo se elimina una base si su owner coincide con el rol `domibase_<slug>_owner` derivado del proyecto; cualquier otro owner provoca rechazo y auditoría del fallo.
- Guard de nombres (`ProjectResourceNaming`): valida slug, exige que `DatabaseName` coincida con el slug y comprueba el límite de 63 bytes de identificadores PostgreSQL antes de construir SQL.
- Bloqueo advisory por base compartido entre provisioning y limpieza; evita compensar recursos de una Ejecución concurrente.
- Si el fallo fue por conflicto de bloqueo (sin pasos exitosos), **no** se compensa: no se tocan recursos ajenos.

## 2. Arquitectura modificada

- `Application.Projects`: nuevos contratos `IProvisioningStepSink`, `DeprovisionOutcome`, `ProvisioningSteps` y `ProjectResourceNaming`.
- `Provisioning.ProjectProvisioner`: pasos envueltos en `RunStepAsync`, compensación/desprovisioning con inspección de catálogo, terminación segura de conexiones y verificación de owner.
- `Application.Projects.ProjectService`: inyecta un `ProvisioningStepSink` que usa el repositorio de control; tras un fallo con recursos creados, dispara compensación y audita `project.compensated` o `project.compensation_failed`.
- `Infrastructure.ControlPlane.EfProjectRepository`: persistencia incremental de operaciones, consulta de intentos y listado para diagnóstico.
- La Connection String privilegiada sigue confinada a `DomiBase.Provisioning`; el Runtime no ve estas operaciones.

## 3. Archivos creados

- `backend/src/DomiBase.Application/Projects/ProjectResourceNaming.cs`
- `backend/tests/DomiBase.UnitTests/ProjectResourceNamingTests.cs`
- `backend/tests/DomiBase.IntegrationTests/ProvisioningLifecycleTests.cs`
- `docs/implementation/p0-2-provisioning.md`

## 4. Archivos modificados

- `backend/src/DomiBase.Application/Projects/IProjectProvisioner.cs`
- `backend/src/DomiBase.Application/Projects/IProjectRepository.cs`
- `backend/src/DomiBase.Application/Projects/ProjectService.cs`
- `backend/src/DomiBase.Infrastructure/ControlPlane/EfProjectRepository.cs`
- `backend/src/DomiBase.Provisioning/ProjectProvisioner.cs`
- `docs/implementation/roadmap.md`
- `README.md`

## 5. Migraciones creadas

Ninguna. Las columnas `status`, `attempt`, `correlation_id`, `started_at` y `completed_at` ya existían en `provisioning_operations`; el cambio es de uso y trazabilidad, no de esquema.

## 6. Pruebas ejecutadas

- `dotnet build backend/DomiBase.sln`: 0 errores, 0 warnings.
- `dotnet test backend/DomiBase.sln`: **194/194** en verde.
  - UnitTests: 116 (7 nuevos de `ProjectResourceNaming`).
  - IntegrationTests: 69 (5 nuevos de ciclo de vida y compensación).
  - SecurityTests: 9.
- PostgreSQL sigue siendo el contenedor `domibase-postgres` en `127.0.0.1:5433`; las pruebas crearon y eliminaron bases/roles con slugs únicos.

Casos nuevos relevantes: pasos persistidos con intento y correlación; eventos `Running -> Succeeded` por paso; desprovisioning idempotente sin afectar otro proyecto; compensación de roles parciales; rechazo de base con owner ajeno.

## 7. Resultados

Los criterios de aceptación P0.2 quedan cubiertos: etapas registradas, detección de recursos parciales, compensación ejecutada, reintentos seguros, errores y estados persistidos, ausencia de duplicados por advisory lock y protección contra recursos de otro proyecto.

## 8. Riesgos identificados

- `DROP DATABASE` y `DROP ROLE` no son transaccionales de forma conjunta; una compensación interrumpida puede dejar roles residuales. El proyecto queda en `Failed`, la limpieza es idempotente y puede reintentarse.
- La terminación de conexiones usa `pg_terminate_backend` sobre la base exacta; una reconexión inmediata de un cliente externo puede hacer fallar el `DROP DATABASE` y requerir reintento.
- El guard depende de que `Project.DatabaseName` no haya sido manipulado en la base de control; por eso la validación rechaza cualquier discrepancia antes de generar SQL.
- No se ha probado aún un fallo inyectado dentro del host ASP.NET Core; las pruebas cubren fallos de PostgreSQL reales y compensación directa.

## 9. Limitaciones pendientes

- P0.1 aún no expone `DELETE /api/v1/projects/{id}`; `DeprovisionAsync` queda listo para ese endpoint.
- P0.3 (rotación/expiración de keys) y P0.4 (keyring de secretos) no han empezado.
- Las decisiones sobre confirmación administrativa y semántica de claves secretas siguen pendientes; P0.2 no las toca.
- Sin backup/restauración: la compensación elimina recursos, no los respalda.

## 10. Siguiente fase

**P0.1 — Eliminación de proyectos**: usar `DeprovisionAsync` desde `ProjectService.DeleteAsync`, con estado `Deleting`, revocación de keys, limpieza de credenciales, auditoría, idempotencia y confirmación de slug. Después: P0.3 y P0.4.

> **Actualización:** las limitaciones de esta sección se resolvieron después: P0.1, P0.3 y P0.4 están completadas y documentadas. Las decisiones sobre confirmación administrativa y claves secretas se fijaron en P0.1 y P0.3 respectivamente.
