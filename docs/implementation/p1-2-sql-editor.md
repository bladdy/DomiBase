# P1.2 — SQL Editor administrativo acotado

**Fecha:** 2026-11-17  
**Estado:** completado y verificado.  
**Objetivo:** ofrecer al propietario una consola de consulta acotada en el panel, sin exponer SQL arbitrario, sin filtrar SQL interno en errores ni logs y sin sacar la administración del Control Plane.

## 1. Funcionalidades implementadas

### Control API

Nueva ruta privada bajo `/api/v1` (protegida por el middleware administrativo existente, **nunca** bajo `/rest/v1`):

| Método | Ruta | Acción |
| --- | --- | --- |
| `POST` | `/api/v1/projects/{projectId}/sql/query` | Ejecuta una única sentencia de solo lectura contra la base del proyecto indicado. |

Cuerpo: `{ "sql": "...", "maxRows"?: number, "timeoutMs"?: number }`.  
Respuesta: `{ columns, rows, rowCount, truncated, durationMs }`.

**Límites aplicados server-side:**

| Parámetro | Por defecto | Rango / tope |
| --- | ---: | --- |
| Longitud de `sql` | — | 8.000 caracteres |
| `maxRows` | 100 | 1–1.000 (se acota, no se rechaza) |
| `timeoutMs` | 10.000 | 100–30.000 (se acota, no se rechaza) |

Errores: validación → `400 VALIDATION_ERROR`; timeout/permiso/estado → `409 CONFLICT`; ausencia de token → `401 UNAUTHORIZED`.

### Panel React

Pestaña **SQL** en `frontend/admin`, con:

- Editor de texto con sentencia inicial y `aria-describedby`.
- Campos de máximo de filas y timeout con los mismos rangos del servidor.
- Botón *Ejecutar consulta* con estados de ejecución y cancelación mediante `AbortController` al cambiar de proyecto o desmontar.
- Tabla de resultados con columnas, celdas JSON-safe, metadatos de filas/duración y aviso de truncamiento.
- Aviso explícito de que la operación es solo lectura y de que la sentencia no se registra.

## 2. Decisiones de diseño

- **Solo lectura real, no solo cosmética.** El validador léxico acepta únicamente una sentencia `SELECT`/`WITH`; el runner aplica además `SET TRANSACTION READ ONLY`, de modo que PostgreSQL es la última línea de defensa.
- **El validador blanquea literales, identificadores entre comillas, comentarios y dollar-quotes** antes de buscar palabras prohibidas. Así, `SELECT 'DROP TABLE x'` es válido, pero `SELECT 1; DROP TABLE x` no. Se bloquean DML/DDL, control de transacciones, `INTO`, funciones de escritura (`nextval`, `set_config`) y funciones de sistema peligrosas (`pg_read_file`, `dblink`, etc.).
- **Una sola sentencia.** Se permite un `;` final, nunca dos sentencias.
- **Identidad y privilegios derivados del proyecto.** El runner abre la conexión privilegiada, ejecuta `SET LOCAL ROLE "<owner>"` con quoting seguro, fija `statement_timeout` con `set_config(..., true)` (local a la transacción) y confirma o revierte en una transacción corta. El cliente nunca envía un rol ni un `search_path`.
- **Resultados tipados sin SQL interno.** La consulta se envuelve como `SELECT q.*, to_jsonb(q) AS "__domibase_row" FROM (<statement>) AS q LIMIT @limit`. `@limit` es parametrizado; un registro extra revela truncamiento sin materializar un result set ilimitado. `to_jsonb` conserva los tipos PostgreSQL como JSON.
- **Auditoría sin la sentencia.** Se registra `sql.query` con base, filas, truncamiento, duración, límites y `sqlFingerprint = sha256:<16 hex>`. El SQL crudo nunca se persiste ni se devuelve en errores.
- **Traducción de errores.** Los `PostgresException` se convierten a mensajes fijos. `42601` (sintaxis) no devuelve `MessageText`, porque puede contener fragmentos de SQL; `57014` (timeout) y `42501` (permiso) se mapean a `CONFLICT`.
- **Reutilización.** El runner usa la misma `IProvisioningConnectionFactory` y `ProjectResourceNames` que provisioning, DDL y migraciones. El orquestador usa `ProjectSchemaResolver`, compartido con P1.1/P1.3, para exigir proyecto existente y `Ready`. No se duplican conexiones ni guards.
- **Sin secretos en el navegador.** El panel solo envía el token administrativo que ya usa el resto de la Control API. No hay credenciales PostgreSQL ni roles físicos en el front end.

## 3. Archivos creados

**Backend — Application**
- `backend/src/DomiBase.Application/Query/SqlQuery.cs` — `SqlQueryResult`, `QueryExecutionException`, `IProjectQueryRunner`.
- `backend/src/DomiBase.Application/Query/ReadOnlySqlValidator.cs` — validador léxico.
- `backend/src/DomiBase.Application/Query/SqlQueryService.cs` — orquestación, límites, ejecución y auditoría.

**Backend — Provisioning**
- `backend/src/DomiBase.Provisioning/Query/ProjectQueryRunner.cs` — transacción `READ ONLY`, timeout, `to_jsonb`, row cap.

**Backend — API**
- `backend/src/DomiBase.Api/Endpoints/SqlQueryControlEndpoints.cs` — endpoint `POST /sql/query`.

**Frontend**
- `frontend/admin/src/components/SqlSection.tsx` — pestaña SQL.

**Tests**
- `backend/tests/DomiBase.UnitTests/ReadOnlySqlValidatorTests.cs`
- `backend/tests/DomiBase.IntegrationTests/SqlQueryTests.cs`

**Documentación**
- `docs/implementation/p1-2-sql-editor.md`

## 4. Archivos modificados

- `backend/src/DomiBase.Api/Program.cs` — mapeo de `MapSqlQueryControlEndpoints()`.
- `backend/src/DomiBase.Api/ControlPlane/ControlPlaneServiceCollectionExtensions.cs` — registro de `SqlQueryService`.
- `backend/src/DomiBase.Provisioning/ProvisioningServiceCollectionExtensions.cs` — registro de `IProjectQueryRunner`.
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs` — 3 security tests de `/sql/query`.
- `frontend/admin/src/lib/control-client.ts` — tipos `SqlQueryRequest`/`SqlQueryResult` y `runSqlQuery()`.
- `frontend/admin/src/lib/control-client.test.ts` — 4 tests del cliente SQL.
- `frontend/admin/src/App.tsx` — pestaña, título y panel SQL.
- `frontend/admin/src/components/TabRail.tsx` — `SectionId` incluye `sql`.
- `frontend/admin/src/styles.css` — editor, resultados y color de sección.
- `README.md`, `docs/implementation/roadmap.md`, `docs/architecture/overview.md`, `docs/architecture/security.md`, `docs/audits/DOMIBASE_TECHNICAL_AUDIT.md`, `docs/implementation/p1-3-migrations.md` — estado y cifras actualizados.

## 5. Migraciones creadas

**Ninguna.** No hay esquema nuevo ni columnas en `domibase_control`; la auditoría reutiliza `audit_entries` y la única tabla de historial sigue siendo `domibase_migrations.applied` de P1.3.

## 6. Pruebas ejecutadas

```powershell
dotnet build backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
dotnet test  backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
cd frontend/admin
npm.cmd test
npm.cmd run build
```

Nota de entorno: el proceso manual `DomiBase.Api` bloquea los DLL de `bin\Debug\net10.0`, por lo que se usa `-p:BaseOutputPath=bin-p11\`. En PowerShell, `npm.ps1` está bloqueado por firma: se invoca `npm.cmd`.

| Suite | Antes de P1.2 | Después de P1.2 |
| --- | ---: | ---: |
| UnitTests | 180 | **212** |
| IntegrationTests | 88 | **102** |
| SecurityTests | 17 | **20** |
| **Total backend** | **285** | **334** |
| Panel (Vitest) | 14 | **18** |

Casos nuevos relevantes:

- 32 tests del validador: `SELECT`/`WITH`, literales con `DROP`, comentarios, dollar-quotes, identificadores `"update"`, rechazo de DML/DDL, `INTO`, `nextval`, `set_config`, `pg_read_file`, múltiples sentencias, longitud y comandos sin cerrar.
- 14 tests de integración: columnas y filas JSON, CTE, truncamiento con `maxRows`, siete variantes de escritura/inyección sin tocar datos, tabla inexistente sin SQL en el error, timeout por `pg_sleep`, auditoría con fingerprint y sin sentencia, y aislamiento entre proyectos.
- 3 tests de seguridad: `/sql/query` sin token, con token erróneo y con solo `apikey` público → `401`.
- 4 tests del cliente: ruta y cuerpo, omisión de límites, error canónico y `AbortSignal`.
- Build del panel: `tsc --noEmit` y Vite sin errores.

## 7. Resultados

- Build backend limpio: **0 errores, 0 warnings**.
- Suite backend: **334/334 tests en verde** en esta entrega; estado acumulado tras P1.5: **367/367 tests backend** y **22/22 tests del panel**. Estado al cerrar P1.4: **383/383 tests backend** y **35/35 tests del panel** (instantánea histórica; el total vigente está en `docs/implementation/roadmap.md`).
- Panel: **18/18 tests** en esta entrega (22 tras P1.5) y build de producción correcto.
- PostgreSQL 18.7 en Docker (`127.0.0.1:5433`) sigue healthy; no se detuvieron contenedores.
- Ninguna credencial, sentencia SQL ni nombre de rol físico aparece en respuestas de error ni en la auditoría.
- No se hicieron commits, pushes ni despliegues.

## 8. Riesgos identificados

- **Superficie de consulta:** aunque es de solo lectura, un propietario con token administrativo puede inspeccionar datos de un proyecto. Es el alcance previsto: el panel es privado y el token es administrativo.
- **Coste de consultas:** el timeout y el row cap limitan el resultado, pero una sentencia compleja puede consumir CPU. El auditor `sql.query` permite detectar abusos. La cuota por proyecto de P1.4 cubre los planos Runtime; esta consola administrativa sigue sin una cuota dedicada.
- **Ambigüedad léxica:** el validador es conservador y puede rechazar sentencias válidas que usen palabras prohibidas como identificador sin comillas. Es preferible un falso positivo a una escritura accidental.
- **Doble ejecución para truncamiento:** no; la fila extra se detecta en el mismo `DataReader`, sin una segunda consulta.

## 9. Limitaciones pendientes (fuera de P1.2)

- No se permite escritura ni DDL desde este editor: para eso están P1.1 (operaciones tipadas) y P1.3 (migraciones versionadas).
- No hay historial de consultas ejecutadas en el panel; solo la auditoría del servidor con fingerprint.
- No hay guardado/exportación de resultados ni paginación; el límite es un conjunto acotado de filas.
- No hay editor de migraciones ni de esquema en el panel; la pestaña Esquema sigue siendo solo de lectura.

## 10. Siguiente fase

Las restantes entregas P1–P10 se planificarán individualmente. Ninguna se marca completada sin su propio build, pruebas y documentación.
