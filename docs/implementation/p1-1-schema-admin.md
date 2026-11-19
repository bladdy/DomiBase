# P1.1 — Administración de esquema y políticas RLS

**Fecha:** 2026-11-17  
**Estado:** completado y verificado.  
**Objetivo:** que el propietario pueda crear y modificar tablas, columnas, índices y políticas RLS de un proyecto desde la Control API, sin conectarse a PostgreSQL ni exponer credenciales de propietario.

## 1. Funcionalidades implementadas

Operaciones administrativas bajo `/api/v1/projects/{projectId}/schema`:

| Método | Ruta | Acción |
| --- | --- | --- |
| `POST` | `/tables` | Crear tabla con columnas validadas, tipos allowlisted, defaults literales y foreign keys opcionales. RLS se habilita automáticamente. |
| `DELETE` | `/tables/{table}` | Eliminar tabla. |
| `POST` | `/tables/{table}/columns` | Añadir columna. |
| `PATCH` | `/tables/{table}/columns/{column}` | Renombrar, cambiar tipo y/o nullable. |
| `DELETE` | `/tables/{table}/columns/{column}` | Eliminar columna. |
| `POST` | `/tables/{table}/indexes` | Crear índice (simple o compuesto, único o no). |
| `DELETE` | `/tables/{table}/indexes/{index}` | Eliminar índice, solo si pertenece a la tabla indicada. |
| `GET` | `/tables/{table}/policies` | Consultar políticas con comandos y roles lógicos (`anon`/`authenticated`). |
| `POST` | `/tables/{table}/policies` | Crear política, conceder GRANT mínimo y habilitar RLS. |
| `PATCH` | `/tables/{table}/policies/{policy}` | Reemplazar la definición de la política de forma atómica. |
| `DELETE` | `/tables/{table}/policies/{policy}` | Eliminar política. |

Las rutas de solo lectura preexistentes (`/tables`, `/views`, `/functions`) no cambian.

**Decisiones de seguridad aplicadas:**

- No existe un endpoint de SQL arbitrario. El cliente envía una operación tipada; el servidor construye el DDL.
- Identificadores: `[a-z_][a-z0-9_]*`, máximo 63 caracteres, sin prefijo `pg_`.
- Tipos permitidos: `uuid`, `text`, `varchar(n≤8000)`, `int2`, `int4`, `int8`, `boolean`, `timestamp`, `timestamptz`, `date`, `json`, `jsonb`, `numeric`.
- Defaults: literales JSON con escape de comillas, números, booleanos y solo dos expresiones fijas (`gen_random_uuid()`, `now()`). Se rechazan barras invertidas, separadores de sentencia y caracteres de control.
- Políticas: comandos `SELECT|INSERT|UPDATE|DELETE`, roles lógicos `anon|authenticated` (los nombres físicos se derivan del slug del proyecto), y expresiones `USING`/`WITH CHECK` restringidas a `true`, `false` o `project_id = current_setting('domibase.project_id', true)::uuid`.
- Una política se modela como `FOR ALL` + `GRANT` de solo los comandos seleccionados. Así el alcance real de la política son los privilegios, sin codificar comandos en el nombre.
- Las tablas nuevas se crean con `ENABLE ROW LEVEL SECURITY`: acceso denegado por defecto hasta que se definan GRANT y política.
- Cada cambio se ejecuta en transacción corta con `SET LOCAL ROLE` al rol owner derivado del proyecto. El contexto nunca se comparte entre conexiones.
- Errores PostgreSQL se traducen a mensajes seguros (sin SQL interno, sin nombres de otros proyectos). El SQLSTATE se conserva solo como `hint`.
- Toda operación exitosa escribe una entrada de auditoría con acción, base, tabla y objeto; nunca con SQL ni secretos.

## 2. Arquitectura modificada

- **`DomiBase.Application.Schema`**: añade contratos tipados (`SchemaMutation` y derivados), validador único (`SchemaDdlValidator`), abstracción privilegiada (`IProjectSchemaAdmin`) y orquestador (`SchemaAdminService`). La capa Application sigue sin referenciar Npgsql.
- **`DomiBase.Provisioning.Admin`**: `ProjectSchemaAdmin` reutiliza `IProvisioningConnectionFactory`, abre la base del proyecto, aplica `SET LOCAL ROLE` al owner y ejecuta el DDL validado. Es el único consumidor nuevo de la cadena privilegiada.
- **`DomiBase.Api`**: `SchemaControlEndpoints` pasa de inspector de solo lectura a módulo completo de administración de esquema. Los DTO convierten JSON a operaciones tipadas antes de llegar a Application.
- **Separación de planos:** ninguna ruta nueva está bajo `/rest/v1`. El middleware administrativo existente protege `/api/v1`; las claves públicas no tienen efecto sobre estas rutas.

## 3. Archivos creados

**Backend — Application**
- `backend/src/DomiBase.Application/Schema/SchemaMutation.cs`
- `backend/src/DomiBase.Application/Schema/SchemaDdlValidator.cs`
- `backend/src/DomiBase.Application/Schema/IProjectSchemaAdmin.cs`
- `backend/src/DomiBase.Application/Schema/SchemaAdminService.cs`

**Backend — Provisioning**
- `backend/src/DomiBase.Provisioning/Admin/ProjectSchemaAdmin.cs`

**Tests**
- `backend/tests/DomiBase.UnitTests/SchemaDdlValidatorTests.cs`
- `backend/tests/DomiBase.IntegrationTests/SchemaAdminFixture.cs`
- `backend/tests/DomiBase.IntegrationTests/SchemaAdminTests.cs`

**Documentación**
- `docs/implementation/p1-1-schema-admin.md`
- `docs/audits/DOMIBASE_TECHNICAL_AUDIT.md` (punto de entrada normalizado hacia la auditoría base y su verificación)

## 4. Archivos modificados

- `backend/src/DomiBase.Api/Endpoints/SchemaControlEndpoints.cs` — nuevas rutas y DTO.
- `backend/src/DomiBase.Api/ControlPlane/ControlPlaneServiceCollectionExtensions.cs` — registro de `SchemaAdminService`.
- `backend/src/DomiBase.Provisioning/ProvisioningServiceCollectionExtensions.cs` — registro de `IProjectSchemaAdmin`.
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs` — 3 tests nuevos de autenticación de rutas DDL.
- `README.md`, `docs/implementation/roadmap.md`, `docs/architecture/overview.md`, `docs/architecture/security.md` — estado y contratos actualizados.

## 5. Migraciones creadas

**Ninguna.** P1.1 no añade entidades EF Core ni columnas a `domibase_control`: toda la configuración de esquema vive en la base de cada proyecto y se materializa mediante DDL. La auditoría reutiliza la tabla `audit_entries` existente.

## 6. Pruebas ejecutadas

```powershell
dotnet build backend/DomiBase.sln --v:q
dotnet test backend/DomiBase.sln --v:q
```

Nota de entorno: el puerto/proceso `DomiBase.Api` estaba en uso durante la verificación, así que se redirigió la salida de compilación con `-p:BaseOutputPath=bin-p11\` para no interrumpir la instancia manual. El resultado funcional es idéntico.

Desglose:

| Suite | Antes de P1.1 | Después de P1.1 |
| --- | ---: | ---: |
| UnitTests | 125 | **169** |
| IntegrationTests | 77 | **83** |
| SecurityTests | 11 | **14** |
| **Total backend** | **213** | **266** |

Casos nuevos relevantes:

- Crear tabla → `relrowsecurity = true`, columnas y defaults reales.
- Renombrar/añadir/eliminar columna y crear/eliminar índice contra catálogo PostgreSQL.
- Eliminar índice declarando la tabla equivocada → `400`.
- Crear política `anon` SELECT+INSERT con plantilla de inquilino → INSERT y SELECT por Runtime; otra fila con `project_id` ajeno queda invisible.
- Reemplazar la política con `true` → la segunda fila se hace visible; eliminar la política → SELECT vacío por RLS.
- Inyección en nombre de tabla, tipo, columna, índice y expresión de política → `400 VALIDATION_ERROR` y tabla centinela intacta.
- Proyecto desconocido → `404`; DDL sobre tabla que solo existe en otro proyecto → `400` sin modificarla.
- Auditoría de `schema.table.create` persistida en `domibase_control`.
- Rutas DDL sin token, con token erróneo y con solo `apikey` público → `401`.

## 7. Resultados

- Build limpio: **0 errores, 0 warnings**.
- Suite completa: **266/266 tests en verde**.
- PostgreSQL 18.7 en Docker (`127.0.0.1:5433`) sigue healthy; no se detuvieron ni modificaron contenedores.
- Ninguna credencial, SQL interno ni nombre de rol físico aparece en respuestas de error.
- La API manual en ejecución no fue interrumpida.

## 8. Riesgos identificados

- **DDL privilegiado:** la superficie de administración ejecuta sentencias como rol owner. El riesgo se mitiga con validación estricta, allowlists y ausencia total de SQL arbitrario. Cualquier nueva operación debe pasar por el mismo validador.
- **Expresiones de RLS limitadas:** una futura necesidad de políticas complejas no debe resolverse concatenando texto del cliente; requiere un parser/AST seguro o plantillas adicionales revisadas.
- **GRANT como alcance de comando:** al eliminar una política no se revocan sus GRANT. RLS sigue denegando filas, pero un cambio futuro de política podría ampliar acceso si no se revisan los privilegios. Está documentado y cubierto por el test de SELECT vacío tras borrado.
- **Concurrencia:** no hay bloqueo exclusivo por tabla durante DDL; PostgreSQL gestiona los candados de catálogo. Operaciones simultáneas sobre el mismo objeto pueden terminar con `409`/`400` esperado.
- **Carga del validador:** los defaults literales se limitan a 256 caracteres y las tablas a 100 columnas para evitar DDL desproporcionado.

## 9. Limitaciones pendientes (fuera de P1.1)

- No hay SQL Editor (P1.2) ni migraciones versionadas por proyecto (P1.3).
- No hay administración de constraints/relaciones más allá de `PRIMARY KEY`, `UNIQUE` y `REFERENCES` en la definición de columna.
- No hay creación/modificación de vistas ni funciones desde la API; siguen siendo solo inspeccionables.
- Catálogo de extensiones resuelto en P1.5 (`p1-5-extensions.md`): allowlist de diez extensiones `trusted`, listado en vivo y DDL idempotente desde la Control API y el panel. El constructor de migraciones del panel reutiliza estas mismas allowlists. Estado acumulado en esa entrega: **367 tests backend** y **34 tests del panel**. Estado al cerrar P1.4: **383 tests backend** y **35 tests del panel** (instantánea histórica, `docs/implementation/p1-4-rate-limits.md`; el total vigente está en `docs/implementation/roadmap.md`).
- El panel React todavía no expone estas operaciones; la pestaña de esquema sigue siendo solo de lectura.
- Los roles lógicos soportados son `anon` y `authenticated`; no se aceptan roles arbitrarios por diseño.

## 10. Siguiente fase

**P1.2 — SQL Editor administrativo** en el panel React, apoyado en una nueva Control API de consulta acotada: solo lecturas por defecto, DDL mediante mecanismo administrativo explícito, límite de filas, timeout, duración y auditoría. Debe reutilizar la conexión privilegiada y el validador de P1.1; nunca debe exponerse bajo `/rest/v1`.

Alternativamente, si se prefiere cerrar antes la gestión de base de datos, **P1.3 — migraciones SQL versionadas por proyecto** puede ejecutarse antes que el editor, porque puede apoyarse directamente en las operaciones DDL ya validadas.
