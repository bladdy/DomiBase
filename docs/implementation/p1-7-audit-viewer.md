# P1.7 — Visor de auditoría y logs de provisioning

**Fecha:** 2026-11-18  
**Estado:** implementado, probado y documentado.  
**Alcance:** lectura acotada de la traza administrativa y del historial de pasos de provisioning desde la Control Plane, con filtros validados, redacción de secretos y una pestaña **Auditoría** en el panel privado. No se construyó ni publicó la imagen Docker de la API; no se tocó el homelab.

## Diagnóstico y decisiones

Antes de P1.7 el backend ya escribía `audit_entries` y `provisioning_operations`, pero esa información solo era consultable con SQL directo. El panel no podía responder preguntas operativas básicas —qué acción administrativa ocurrió, cuándo y sobre qué proyecto— sin acceder a la base de control.

- Se mantiene **un único host** ASP.NET Core. Las dos rutas nuevas viven bajo `/api/v1` y usan el middleware administrativo; no existe lectura de auditoría bajo `/rest/v1` y las claves públicas no la autorizan.
- `AuditEntry.ProjectId` es `Guid?`: no toda auditoría es de proyecto (por ejemplo, el arranque). La escritura lo fija **explícitamente** para los eventos que sí pertenecen a uno; los eventos `api_key.*` conservan `TargetId` con el id de la clave y además llevan el proyecto en `details`/`ProjectId`. La migración rellena el histórico con dos `UPDATE` guiados por regex UUID, de modo que datos antiguos mal formados no rompen la migración.
- `AuditQuery.Parse` valida parámetros desconocidos, charset de tokens (64 caracteres como máximo), fechas, rango no invertido, `limit` 1–200 y `offset` 0–1.000.000. `EfProjectRepository.ListAuditEntriesAsync` convierte cada condición en un parámetro EF Core: el cliente no envía SQL, identificadores de tabla ni fragmentos de consulta.
- `AuditRedactor` es defensa en profundidad al serialize: enmascara claves `db_public_*`/`db_secret_*`, pares JSON con nombres de password/secret/token/api key, asignaciones `PGPASSWORD=…` y JWT, y trunca `details`/`Error` a 4000 caracteres. Los escritores ya evitan guardar secretos; el redactor cubre filas históricas o escritas a mano.
- El visor del panel es **global**: los filtros de acción, actor, tipo de objetivo y fechas se aplican a todas las entradas de la base de control. El id de proyecto se usa solo para la verificación directa de API y para la sección de pasos del proyecto activo. Mezclar ambos criterios habría ocultado auditoría legítima de otros proyectos.
- No se añadió un diálogo: la pestaña reutiliza `ControlClient`, `TabRail`, `Notice`, `empty-inline`, `.leaf__group` y las clases de tabla/diálogo existentes.

## Migración

`20261118045153_AddAuditProjectId`, aplicada **solo** a `domibase_control` local:

- Añade `audit_entries.project_id` (`uuid`, nullable).
- Backfill: `target_id` cuando `target_type = 'project'` y `details ->> 'ProjectId'` para el resto, ambos solo si coinciden con un UUID regex.
- Crea `IX_audit_entries_project_id`.
- `Down` elimina índice y columna. La API no aplica migraciones al arrancar.

## Contrato Control Plane

### `GET /api/v1/audit`

| Parámetro | Regla |
| --- | --- |
| `projectId` | GUID válido; acota por `ProjectId`. |
| `action`, `actor`, `targetType` | `^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$`; comparación exacta. |
| `from`, `to` | `DateTimeOffset` invariante; `from <= to`. |
| `limit` | 1–200, default 50. |
| `offset` | 0–1.000.000, default 0. |

Cualquier otra clave devuelve **400 `VALIDATION_ERROR`** con el listado de parámetros permitidos. La respuesta es `{ items, total, limit, offset }`; cada `item` expone `id`, `occurredAt`, `actor`, `action`, `targetType`, `targetId`, `projectId`, `details` y `correlationId`. `details` viaja redactado. La ordenación es `occurredAt DESC`, con `id DESC` como desempate, y `total` se calcula antes de `Skip`/`Take`.

### `GET /api/v1/projects/{projectId:guid}/provisioning-operations`

Devuelve la lista acotada a ese proyecto: `id`, `projectId`, `step`, `status`, `attempt`, `error` (redactado), `correlationId`, `startedAt` y `completedAt`. Si el proyecto no existe, responde **404 `NOT_FOUND`**. Se resuelve por metadatos del servidor: el solicitante no elige base, rol, SQL ni ruta.

### Escritores

Las acciones que ya escribían auditoría (`project.*`, `api_key.*`, `schema.*`, `migration.*`, `sql.query`, `project.rate_limit.updated`, `project.backup.*`) fijan ahora `ProjectId` cuando corresponde y pasan `Details` por el mismo redactor que aplica a la lectura, de modo que el visor y el almacenamiento coinciden.

## Panel

La pestaña **Auditoría** ofrece:

- Filtros por acción, actor, tipo de objetivo y rango de fechas; se envían como parámetros allowlistados desde `ControlClient.listAudit` y se pueden limpiar.
- Paginación con `limit` 20/50/100, botones Anterior/Siguiente y estado `role="status" aria-live="polite"` con el rango y el total.
- Tabla semántica de entradas con `caption` solo para lectores de pantalla, `th[scope="col"]`, `th[scope="row"]` y una columna de detalles que muestra la redacción real.
- Segunda tabla con los pasos de provisioning del proyecto seleccionado; si no hay proyecto, indica que hay que seleccionarlo.
- `AbortController` y contadores de generación para descartar respuestas obsoletas al cambiar de filtro o de proyecto.

### Guía Modern Web: citas verbatim y aplicación

| Guía | Cita literal | Aplicación en P1.7 |
| --- | --- | --- |
| `responsive-table` | “Use standard `<table>` elements with `<thead>`, `<tbody>`, and `<th>` elements.” | Ambas tablas usan `<table>`, `<caption>`, `<thead>`, `<tbody>`, `<th scope="col">` y `<th scope="row">`. |
| `responsive-table` | “Use `@container` queries instead of `@media` queries.” | El layout del visor se adapta con `@container (max-width: 720px)` sobre `.leaf`/`.audit-table-wrap`; el `@media` global sigue siendo solo para el riel de pestañas. |
| `responsive-table` | “MANDATORY: Hide column header from layout and screen readers.” | En el modo apilado se aplica `display: none` a `thead` y cada celda inyecta su etiqueta con `td::before { content: attr(data-label) ": "; }`. |
| `forms` | “DO use `<fieldset>` and `<legend>` (with `<legend>` as the first child) to group related controls.” | Los cinco filtros y sus botones están dentro de `<search><form><fieldset><legend>Filtros</legend>…`. |
| `forms` | “DO always associate `<label>` with its input using `for` and `id`.” | Cada campo usa `htmlFor`/`id`; el selector de tamaño de página también tiene `<label htmlFor>`. |
| `accessibility` | “DO maintain a visible `:focus-visible` indicator on all controls. Never disable focus outlines (`outline:none`) without providing a high-contrast alternative.” | El sistema visual conserva `:focus-visible { outline: 3px solid #f5d76e; outline-offset: 2px; }` para todos los controles, incluidos los nuevos. |

## Seguridad

- Solo token administrativo (`X-DomiBase-Admin-Token` o `Bearer`) bajo `/api/v1`; sin token, con token erróneo o con solo `apikey` público se deniega.
- Parámetros desconocidos y caracteres no permitidos se rechazan antes de tocar la base; nada se interpola en SQL.
- La lectura no escribe auditoría: el visor no contamina la traza que muestra.
- Secretos enmascarados en almacenamiento y respuesta; ni el panel ni los tests imprimen token, clave, prefijo ni ruta.
- Auditoría y provisioning siguen siendo exclusivos de la Control Plane; `/rest/v1` no expone ninguna de las dos rutas.

## Verificación

- `dotnet build backend/DomiBase.sln`: **0 errores, 0 warnings**.
- `dotnet test backend/DomiBase.sln --no-build`: **458/458** (288 unitarios, 129 de integración, 41 de seguridad). Nuevos archivos: `AuditQueryTests`, `AuditRedactorTests`, `AuditServiceTests`, `AuditViewerTests` y `AuditSecurityTests`.
- `npm.cmd test`: **45/45**; `npm.cmd run build`: correcto.
- E2E real `node scripts/dev/verify-audit-panel.mjs`: **16/16** comprobaciones con PostgreSQL y API reales. Crea 21 claves para forzar paginación, comprueba redacción del servidor en `api_key.create`, aplica filtros y paginación desde el panel, verifica los pasos de provisioning, revisa 390×844 sin desbordamiento, exige consola limpia y elimina el proyecto temporal sin imprimir secretos.
- Capturas: `.impeccable/review/audit-mobile.png` y `.impeccable/review/audit-desktop.png`.

Criterio de aceptación cumplido: un administrador puede reconstruir acciones reales y el historial de provisioning desde el panel, sin secretos, sin SQL arbitrario y sin exponer la base de control al Runtime público.
