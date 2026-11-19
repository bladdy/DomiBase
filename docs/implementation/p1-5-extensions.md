# P1.5 — Catálogo de extensiones PostgreSQL

**Fecha:** 2026-11-17  
**Estado:** completado y verificado.  
**Objetivo:** permitir al propietario consultar e instalar/desinstalar un conjunto acotado de extensiones PostgreSQL por proyecto, sin SQL arbitrario, sin secretos en el panel y sin exponer administración en el Runtime Plane.

## 1. Funcionalidades implementadas

### 1.1 Allowlist estático

`PostgresExtensionCatalog` (`backend/src/DomiBase.Application/Extensions/`) define diez extensiones `trusted`, verificadas como disponibles en la imagen fijada `postgres:18-alpine` (PostgreSQL 18.7): `btree_gin`, `btree_gist`, `citext`, `hstore`, `intarray`, `ltree`, `pg_trgm`, `pgcrypto`, `unaccent` y `uuid-ossp`.

- `IsAllowed` y `TryGet` usan comparación ordinal exacta: no aceptan variantes de mayúsculas, espacios, sufijos de versión ni fragmentos SQL.
- Cada definición incluye una descripción para el panel; los nombres se exponen en orden determinista.
- No se acepta esquema, versión, rol ni SQL desde el cliente.

### 1.2 Control API

Todas las rutas están bajo `/api/v1`, protegidas por el middleware administrativo existente. **No** se mapbean bajo `/rest/v1`.

| Método | Ruta | Acción |
| --- | --- | --- |
| `GET` | `/api/v1/projects/{projectId}/extensions` | Devuelve el catálogo allowlisted con disponibilidad y estado de instalación en vivo. |
| `POST` | `/api/v1/projects/{projectId}/extensions` | Instala una extensión del catálogo. Cuerpo: `{ "name": "pgcrypto" }`. |
| `DELETE` | `/api/v1/projects/{projectId}/extensions/{name}` | Desinstala una extensión del catálogo. |

Respuesta de listado por extensión:

```json
{
  "name": "pgcrypto",
  "description": "Cryptographic functions such as digest, hmac and gen_random_uuid.",
  "trusted": true,
  "availableVersion": "1.4",
  "installedVersion": "1.4",
  "available": true,
  "installed": true,
  "canInstall": false,
  "canUninstall": true
}
```

Respuesta de mutación: `{ "name": "pgcrypto", "action": "extension.install" }` o `{ "name": "pgcrypto", "action": "extension.uninstall" }`.

Errores: validación → `400 VALIDATION_ERROR`, proyecto ausente → `404 NOT_FOUND`, proyecto no `Ready` o fallo PostgreSQL → `409 CONFLICT`, sin token administrativo → `401 UNAUTHORIZED`. Los mensajes son fijos; el SQLSTATE puede aparecer como `hint`, nunca el SQL ejecutado.

### 1.3 Orquestación y catálogo en vivo

- `ExtensionService` reutiliza `ProjectSchemaResolver` (mismo guardia que P1.1/P1.2/P1.3): el proyecto se resuelve desde metadatos de confianza, debe estar `Ready` y los nombres PostgreSQL se derivan del slug validado.
- `ProjectExtensionCatalogReader` consulta `pg_available_extensions`, `pg_available_extension_versions` y `pg_extension` en la base del proyecto, dentro de una transacción `SET TRANSACTION READ ONLY`. Los nombres allowlisted se envían como parámetro `text[]`; nunca se concatenan como identificadores.
- `ProjectExtensionAdmin` ejecuta `CREATE EXTENSION IF NOT EXISTS` / `DROP EXTENSION IF EXISTS` en transacción corta, con `SET LOCAL ROLE "<owner>"` derivado de metadatos. Es idempotente y compensable por rollback ante error.

### 1.4 Auditoría

`audit_entries` registra `extension.list`, `extension.install` y `extension.uninstall` con `targetType = "project"`, `targetId` y `details` JSON no secretos (base, extensión, conteo). No se almacena DDL, SQLSTATE sensible, credencial ni nombre de rol físico.

### 1.5 Panel privado

Nueva pestaña **Extensiones** en `frontend/admin`, cableada al ciclo de recursos ya existente:

- El cliente `ControlClient` añade `listExtensions`, `installExtension` y `uninstallExtension`, con el mismo token administrativo, `AbortSignal` y envelope de error canónico.
- La vista usa una tabla semántica (`<caption>` oculta, `th scope="col"`, `role="region"` con `tabIndex`), botones por fila y el `ConfirmDialog` nativo existente para desinstalar.
- El panel envía solo el nombre allowlisted; el servidor posee el catálogo, el DDL y la auditoría.

## 2. Decisiones de seguridad

- **Sin SQL arbitrario:** la API no expone ejecución de sentencias; el único texto que cruza la frontera es un nombre del allowlist validado dos veces (Application y Provisioning).
- **Sin escalada de roles:** el cliente nunca envía roles; la mutación usa `SET LOCAL ROLE` al owner derivado de metadatos verificados, con la conexión privilegiada reutilizada del módulo de provisioning.
- **Aislamiento:** proyecto desconocido → `404`; proyecto no `Ready` → `409`; instalación en un proyecto no crea la extensión en otro (verificado contra catálogo real).
- **Sin secretos:** ni respuestas, ni logs, ni auditoría contienen SQL interno, credenciales ni nombres de roles físicos. El `ILogger` solo registra la acción, la extensión, la base y el SQLSTATE.
- **RuntimePlane sellado:** los endpoints `/api/v1/projects/{id}/extensions` no existen bajo `/rest/v1`; las rutas sin token, con token erróneo y con solo `apikey` público devuelven `401`.

## 3. Archivos principales

| Capa | Archivo |
| --- | --- |
| Application | `backend/src/DomiBase.Application/Extensions/PostgresExtension.cs` |
| Application | `backend/src/DomiBase.Application/Extensions/PostgresExtensionCatalog.cs` |
| Application | `backend/src/DomiBase.Application/Extensions/ExtensionService.cs` |
| Provisioning | `backend/src/DomiBase.Provisioning/Catalog/ProjectExtensionCatalogReader.cs` |
| Provisioning | `backend/src/DomiBase.Provisioning/Admin/ProjectExtensionAdmin.cs` |
| API | `backend/src/DomiBase.Api/Endpoints/ExtensionControlEndpoints.cs` |
| Panel | `frontend/admin/src/components/ExtensionsSection.tsx` |
| Panel | `frontend/admin/src/lib/control-client.ts` |

`Program.cs`, los `ServiceCollectionExtensions` de Control Plane y Provisioning y el registro de catálogo se ampliaron sin duplicar conexiones, guards ni autenticación.

## 4. Verificación

| Suite | Antes de P1.5 | Después de P1.5 |
| --- | ---: | ---: |
| UnitTests | 212 | **231** |
| IntegrationTests | 102 | **113** |
| SecurityTests | 20 | **23** |
| **Total backend** | **334** | **367** |
| Panel (Vitest) | 18 | **22** |

Casos nuevos relevantes:

- Catálogo exacto, finito, único, ordenado y rechazo de nombres inyectados o peligrosos (`dblink`, `plpython3u`, `pgcrypto"; DROP EXTENSION …`).
- Validación anterior a la resolución de proyecto y a cualquier conexión (tests con dependencias nulas).
- Listado real desde `pg_available_extensions`/`pg_extension` contra PostgreSQL 18.7 en Docker.
- Install/uninstall idempotentes, con propietario real verificado en `pg_extension.extowner`.
- Aislamiento entre proyectos y rechazo de nombre ausente/contra proyecto desconocido.
- Auditoría `extension.list/install/uninstall` sin DDL, credenciales ni roles físicos.
- Tres tests de seguridad: sin token, token erróneo y solo `apikey` público → `401`.
- Cuatro tests del cliente HTTP: ruta y señal, cuerpo de instalación, `DELETE` con nombre codificado y error canónico.

Comandos ejecutados:

```
dotnet build backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
dotnet test  backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
npm.cmd test
npm.cmd run build
```

Resultados:

- Build backend: **0 errores, 0 warnings**.
- Backend en esta entrega: **367/367 tests en verde** (231 unitarios, 113 de integración, 23 de seguridad). Estado al cerrar P1.4: **383/383 tests backend** y **35/35 tests del panel** (instantánea histórica, `docs/implementation/p1-4-rate-limits.md`; el total vigente está en `docs/implementation/roadmap.md`).
- Panel: **22/22 tests Vitest** y build de producción correcto (`tsc --noEmit` + Vite).
- PostgreSQL 18.7 en Docker (`127.0.0.1:5433`) sigue healthy; no se detuvieron contenedores.
- No se hicieron commits, pushes ni builds/despliegues de imagen Docker.

## 5. Riesgos y limitaciones

- El catálogo es estático: añadir una extensión requiere verificar que es `trusted` en PostgreSQL 18 y actualizar `PostgresExtensionCatalog` con tests; es deliberado para evitar superficie dinámica.
- `DROP EXTENSION` falla con `409` si existen objetos dependientes; el panel muestra el mensaje seguro y no ofrece `CASCADE`.
- No se gestiona versión ni esquema de destino: se usa siempre la versión por defecto y `public`, sin opciones del cliente.
- Solo se exponen extensiones `trusted`; las que requieren superusuario quedan fuera por diseño.
- La pestaña necesita un proyecto en estado `Ready`; si no hay proyecto seleccionado, la vista lo indica y no emite peticiones.

## 6. Siguiente fase

P1.5 cierra la serie de administración acotada del propietario (P1.1 esquema, P1.2 SQL, P1.3 migraciones, P1.5 extensiones). No hay dependencias pendientes para ampliar el panel o introducir nuevas operaciones tipadas.
