# Panel minimalista y superficie vigente

## Estado

Decisión validada el 2026-11-18. Este documento sustituye a `admin-panel-plan.md` como fuente de verdad del panel. Los informes de fases anteriores quedan como registro histórico: describen capacidades que ya no deben aparecer en navegación, exports activos ni documentación de uso.

## Objetivo

Reducir DomiBase a una herramienta privada y comprensible:

`Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK`

Se conservan Control Plane, Runtime Plane, provisioning, RLS y el SDK funcional. No se reescribe la arquitectura.

## Decisiones cerradas

1. **Login:** `POST /admin/v1/login` con `DOMIBASE_ADMIN_USERNAME` / `DOMIBASE_ADMIN_PASSWORD`. Devuelve una sesión `dbsess_` HMAC-SHA-256 con caducidad configurable. Rate limit `admin-login` de 10 intentos/minuto/IP. La sesión se guarda solo en `sessionStorage`.
2. **Token de servicio:** `DOMIBASE_ADMIN_TOKEN` sigue existiendo para automatizaciones y tests, pero el panel no lo usa ni lo solicita.
3. **Navegación global:** solo Proyectos (crear, abrir, editar cupo, eliminar).
4. **Navegación de proyecto:** Resumen, Table Editor, Conexión y Volver a proyectos.
5. **Table Editor:** usa la Control API para DDL tipado y para escrituras de filas; nunca expone la API key pública al navegador ni acepta SQL libre. Las políticas RLS se limitan a `anon_select` / `anon_insert` con expresión fija `true`.
6. **Conexión:** muestra URL de la API, clave pública, metadatos PostgreSQL no secretos, configuración JSON y ejemplo del SDK. La clave completa se muestra solo al crearla o rotarla.
7. **SDK:** el código de Auth/Storage/Realtime/Functions permanece en el repositorio por razones históricas, pero no está en `src/index.ts` ni en la documentación soportada.
8. **Borrado:** tablas, columnas y proyectos requieren confirmación; el borrado de proyecto exige además escribir el slug exacto.
9. **Tipos PostgreSQL:** allowlist ampliada con enteros, `bigint`, `bytea`, `inet`, `interval`, etc.; `IDENTITY` solo sobre tipos enteros y sin `DEFAULT`.

## Contratos usados por el panel

### Login y sesión

- `POST /admin/v1/login` → `{ token, expiresAt }`.
- Cabecera `X-DomiBase-Admin-Token` para todas las rutas `/api/v1`.
- El navegador usa rutas relativas: Vite proxya `/admin` y `/api` a la API (`CONTROL_API_TARGET`). Sin el proxy `/admin`, el login caería en el servidor del panel en `:5190`.
- `ControlClient.login`, `ControlClient.setToken`, `clearSessionToken`, `writeSessionToken`.

### Proyectos

- `GET /api/v1/projects`, `GET /api/v1/projects/{id}`.
- `POST /api/v1/projects`, `POST /api/v1/projects/{id}/provision` (reintento).
- `PATCH /api/v1/projects/{id}/rate-limit` (cupo efectivo).
- `DELETE /api/v1/projects/{id}` con `X-DomiBase-Confirm-Delete: delete` y `{ confirmSlug }` en el cuerpo.

### Esquema y datos

- `GET /api/v1/projects/{id}/schema/tables`
- `GET /api/v1/projects/{id}/schema/tables/{table}/columns`
- `POST|DELETE /api/v1/projects/{id}/schema/tables[/{table}]`
- `POST|DELETE /api/v1/projects/{id}/schema/tables/{table}/columns[/{column}]`
- `GET|POST|PATCH|DELETE /api/v1/projects/{id}/schema/tables/{table}/policies`
- `POST|PATCH|DELETE /api/v1/projects/{id}/data/tables/{table}/rows`

Las filtros de escritura viajan como `f=columna.operador.valor`, acotados y validados en servidor. El panel no envía `role`, `database`, SQL ni `apikey`.

### Claves y conexión

- `GET|POST /api/v1/projects/{id}/keys`
- `DELETE /api/v1/projects/{id}/keys/{keyId}`
- `POST /api/v1/projects/{id}/keys/{keyId}/rotate`

`ControlClient.apiUrl` deriva la URL pública sin credenciales.

## Archivos implicados

```
frontend/admin/src/App.tsx                       sesión, proyectos y cambio de sección
frontend/admin/src/components/TabRail.tsx        tres secciones + volver
frontend/admin/src/components/ProjectsSection.tsx
frontend/admin/src/components/ProjectOverviewSection.tsx
frontend/admin/src/components/TableEditorSection.tsx
frontend/admin/src/components/ConnectionSection.tsx
frontend/admin/src/components/LoginGate.tsx
frontend/admin/src/lib/control-client.ts         HTTP, tipos y errores
frontend/admin/src/styles.css                    estilos de consola
packages/client/src/index.ts                     superficie pública recortada
examples/astro-example/src/lib/domibase.ts       tipos demo locales
```

Backend añadido para el editor:

- `ColumnDefinition.Identity`, validación de `IDENTITY` y allowlist ampliada.
- `SchemaColumn`, `IProjectCatalogReader.ListColumnsAsync`, `ProjectCatalogReader.ColumnsSql`.
- `GET .../columns` y `SchemaColumnResponse`.
- `DataEditorService` y `DataEditorControlEndpoints` para `POST`/`PATCH`/`DELETE` de filas.

## Verificación automática

| Comando | Resultado |
| --- | --- |
| `dotnet test backend/DomiBase.sln --nologo -v minimal` | 491 passed (311 Unit + 134 Integration + 46 Security) |
| `npm test --prefix frontend/admin` | 57 passed |
| `npm run check --prefix frontend/admin` | sin errores |
| `npm test --prefix packages/client` | 70 passed |
| `npm run check --prefix examples/astro-example` | 0 errores |

## Verificación E2E

Ver `docs/implementation/admin-minimal-e2e.md` para el registro ejecutable del flujo completo.
