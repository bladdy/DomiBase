# Inspector de esquema en el panel privado

## Alcance

Extensión de la Fase 6 (Panel privado) sobre la Control API. Permite al propietario ver, en vivo, **tablas, vistas y funciones** del esquema `public` de la base de datos de cada proyecto.

Queda fuera de este inspector: columnas, índices, políticas RLS, DDL, edición de objetos y ejecución de SQL. El inspector es estrictamente de lectura. Las políticas RLS se administran desde la división específica añadida en P2.1 (`p2-1-data-explorer-rls.md`), con plantillas allowlisted y sin SQL libre.

## Decisión

Se eligió **introspección del catálogo PostgreSQL** en lugar de guardar un inventario en `domibase_control`: la pantalla muestra el estado real de la base del proyecto sin copias que puedan quedar obsoletas.

- Solo la Control Plane puede consultarlo. Ninguna ruta de `/rest/v1`, `/auth/v1`, `/storage/v1` ni `/functions/v1` expone estas operaciones.
- El esquema inspeccionado es una constante del servidor (`public`). El cliente no envía esquema, tabla, columna, filtro ni fragmento SQL: solo el `projectId` de la ruta.
- Los nombres de base se validan con `^domibase_[a-z0-9_]+$` antes de abrir una conexión. El `projectId` se resuelve siempre desde metadatos de `domibase_control`.
- El proyecto debe estar en estado `Ready`; si no, la respuesta es `409 CONFLICT`.
- Cada lectura genera una entrada de auditoría (`schema.tables`, `schema.views`, `schema.functions`) con `actor = admin`, `targetType = project`, `targetId` y un `details` no secreto con base, esquema y número de objetos.

## Contrato HTTP

Todas bajo `/api/v1/projects/{projectId:guid}/schema` y protegidas por `X-DomiBase-Admin-Token`:

| Método | Ruta | Respuesta |
| --- | --- | --- |
| `GET` | `/tables` | `[{ "name": "contactos" }]` |
| `GET` | `/views` | `[{ "name": "resumen" }]` (incluye vistas materializadas) |
| `GET` | `/functions` | `[{ "name": "normalizar", "arguments": "valor text", "returnType": "text" }]` |

Errores canónicos: `404 NOT_FOUND` si el proyecto no existe, `409 CONFLICT` si no está `Ready`, `400 VALIDATION_ERROR` si el nombre de base metadata no es válido, `401 UNAUTHORIZED` sin token administrativo válido.

## Capas

- `DomiBase.Application/Schema/SchemaObjects.cs` — DTOs de dominio de aplicación.
- `DomiBase.Application/Schema/IProjectCatalogReader.cs` — contrato de lectura; `Application` no referencia Npgsql.
- `DomiBase.Application/Schema/SchemaService.cs` — resolución del proyecto, validación del nombre de base, exige `Ready`, fija el esquema `public` y audita.
- `DomiBase.Provisioning/Catalog/ProjectCatalogReader.cs` — consultas a `pg_catalog` con la cadena privilegiada existente, `ApplicationName = domibase-catalog`, conexión corta por petición y `SET TRANSACTION READ ONLY`. Solo el esquema constante se pasa como parámetro `@schema`.
- `DomiBase.Api/Endpoints/SchemaControlEndpoints.cs` — endpoints GET y proyección a DTOs de respuesta.

La cadena de conexión privilegiada sigue confinada al proceso de provisioning; el inspector reutiliza `PROVISIONING_DB_CONNECTION_STRING` pero nunca la devuelve ni la registra.

## Consultas

- Tablas: `pg_class` con `relkind IN ('r', 'p')`.
- Vistas: `pg_class` con `relkind IN ('v', 'm')`.
- Funciones: `pg_proc` con `pg_get_function_identity_arguments` y `pg_get_function_result`.

Todas filtran por `n.nspname = @schema` y ordenan por nombre. Esquemas internos (`auth`, esquemas de prueba, `pg_catalog`, `information_schema`) no aparecen.

## Verificación

- `dotnet build backend/DomiBase.sln` — sin warnings ni errores.
- `dotnet test backend/DomiBase.sln --no-build` — **182 tests backend** en verde: 109 unitarios, 64 de integración y 9 de seguridad.
- Tests de integración nuevos (`SchemaControlTests`): objetos reales creados en una base provisionada, vistas materializadas, objetos señuelo en un esquema interno, parámetros de consulta maliciosos ignorados, proyecto inexistente (`404`) y auditoría persistida.
- Tests de seguridad nuevos: sin token, token erróneo y clave pública de Runtime son rechazados en las tres rutas de esquema.
- Panel: 14 tests del cliente HTTP, `npm run check` y `npm run build` en verde.
- Verificación UI con Chrome headless + CDP contra la API real y el proyecto `paneles-solares`: pestaña Esquema activa, tabla `contactos` visible, un solo `h1`, tres agrupaciones, sin desbordamiento en escritorio (1440×900) ni móvil (390×844) y cero errores de consola o respuestas 4xx. Capturas en `.impeccable/review/schema-{desktop,mobile}.png`.
