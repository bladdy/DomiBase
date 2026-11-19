# P1.6 — Backups y restauración lógica por proyecto

**Fecha:** 2026-11-18  
**Estado:** implementado, probado y documentado.  
**Alcance:** copias lógicas `pg_dump`/`pg_restore` por proyecto, gestionadas únicamente desde la Control Plane, con verificación de integridad, auditoría y restauración confirmada. La API sigue siendo un único host; no se construyó su imagen Docker.

## Diagnóstico y decisiones

Antes de P1.6 solo existía persistencia por volumen y la auditoría marcaba la ausencia de backups/restauración como ⛔. La compensación de provisioning elimina recursos, pero no permite recuperar datos borrados ni revertir una migración fallida.

Se evaluó copies físicas del volumen y `pg_basebackup`, pero ambas exigen detener o coordinar el clúster completo y no encajan con un servidor PostgreSQL compartido. Se eligió **volcado lógico por base de proyecto**:

- `pg_dump --format=custom` permite restauración transaccional y conserva esquema, datos, políticas y grants de esa base.
- `pg_restore --clean --if-exists --single-transaction --exit-on-error --no-owner` sustituye los objetos en una sola transacción; si falla, PostgreSQL revierte todo.
- El rol de owner se fija con `PGOPTIONS=-c role=<owner>` y la contraseña administrativa solo vive en `PGPASSWORD` del proceso hijo. Nunca aparece en `argv`, logs ni auditoría.
- En desarrollo no hay binarios de PostgreSQL 18 en el `PATH` de Windows, pero sí en el contenedor `domibase-postgres` (PostgreSQL 18.7). `BACKUP_TOOL_MODE=docker` ejecuta `docker exec -i -e …` y reenvía explícitamente `PGPASSWORD`, `PGOPTIONS`, `PGCONNECT_TIMEOUT` y `PGAPPNAME`. Producción podrá usar binarios directos con `BACKUP_TOOL_MODE=process`; el resto del flujo no cambia.
- La restauración reutiliza `ProjectStatus.Provisioning` como estado transitorio; no se añade un quinto estado. El Runtime rechaza peticiones mientras el proyecto no está `Ready`.
- Un lock advisory por `databaseName`, compartido con el provisioning, impide solapar backup, restore, migraciones o desprovisioning de la misma base.
- Antes de restaurar se recalcula SHA-256 del archivo y se compara con el metadata. Un dump modificado se rechaza con **400 `VALIDATION_ERROR`** sin llegar a tocar PostgreSQL.

## Contrato

### Configuración

| Variable | Default | Descripción |
| --- | --- | --- |
| `BACKUP_ROOT` | obligatoria | Directorio dedicado fuera del repositorio. Las rutas se derivan solo de GUIDs del servidor. |
| `BACKUP_TOOL_MODE` | `process` | `process` o `docker`. |
| `BACKUP_DOCKER_CONTAINER` | — | Obligatoria en modo `docker`. |
| `BACKUP_PG_DUMP_PATH` | `pg_dump` | Ruta del binario en modo `process`. |
| `BACKUP_PG_RESTORE_PATH` | `pg_restore` | Ruta del binario en modo `process`. |
| `BACKUP_TIMEOUT_SECONDS` | 300 | 30–3600 s por herramienta. |
| `BACKUP_MAX_BYTES` | 1073741824 | 1 MiB–8 GiB por copia. |

Los valores inválidos se rechazan al arrancar con `InvalidOperationException` y el nombre de la variable; no hay fallback silencioso. `.env.example` contiene solo placeholders y `.gitignore` excluye `**/backups/`, `*.dump` y `*.dump.tmp`.

### Endpoints Control Plane

```
GET    /api/v1/projects/{id}/backups
POST   /api/v1/projects/{id}/backups
GET    /api/v1/projects/{id}/backups/{backupId}/download
DELETE /api/v1/projects/{id}/backups/{backupId}
POST   /api/v1/projects/{id}/backups/{backupId}/restore
       X-DomiBase-Confirm: <slug exacto del proyecto>
```

- Protegidos por el middleware administrativo de `/api/v1`. Sin token, o con solo una clave pública `apikey`, devuelven 401. No existe ninguna ruta de backups bajo `/rest/v1`.
- `POST` y `DELETE` no aceptan cuerpo: el proyecto, el archivo y el rol PostgreSQL se resuelven desde metadatos de confianza.
- La respuesta expone `id`, `projectId`, `format`, `sizeBytes`, `sha256` y `createdAt`. Nunca expone rutas, roles, contraseñas ni SQL interno. Las listas están acotadas al `projectId` de la URL, aunque se solicite un `backupId` de otro proyecto (404).
- La descarga usa `Content-Disposition: attachment` con un nombre derivado del GUID; el token administrativo viaja en cabecera, nunca en la URL.
- Cada operación escribe auditoría: `project.backup.created`, `.failed`, `.deleted`, `.restore_started`, `.restored` y `.restore_failed`, sin secretos.

### Almacenamiento y ciclo de vida

- Formato en disco: `{BACKUP_ROOT}/{projectId:N}/{backupId:N}.dump`, validado de nuevo por regex antes de leer, inspeccionar, confirmar o borrar. Las rutas relativas con `..` y las rutas absolutas se rechazan.
- La escritura usa un temporal `{backupId:N}.dump.tmp` y un move atómico: un volcado incompleto nunca aparece en el historial. Si la herramienta falla, se limpia el temporal.
- La restauración termina las conexiones activas de esa base y llama a `NpgsqlConnection.ClearAllPools()` en el host; el contexto RLS y el rol nunca sobreviven a la transacción ni se filtran entre peticiones.
- Migración EF `20261118040353_AddProjectBackups`: tabla `project_backups` en `domibase_control`, con índice por proyecto y borrado en cascada. Se generó con `dotnet ef migrations add` y se aplicó **solo** a la base de control local. La API no aplica migraciones al arrancar.

## Panel privado

Nueva pestaña **Backups** con tabla semántica de copias (ID, fecha, tamaño, huella y acciones), botones Crear/Descargar/Restaurar/Eliminar y los estados `Ready`/no `Ready`. Reutiliza `ControlClient`, `ConfirmDialog`, `Notice`, `TabRail`, el token en memoria de pestaña y las clases visuales del panel. El botón de confirmación de restauración permanece deshabilitado hasta que se escribe el `slug` exacto; el servidor vuelve a comprobarlo.

### Modern Web Guidance

- **`responsive-table`**: la vista usa un `<table>` con `<caption>`, `<thead>`, `<tbody>`, `<th scope="col">` y `<th scope="row">`, dentro del contenedor con desbordamiento existente, en vez de sustituir la información por tarjetas o `div`s; la verificación en 390×844 no mostró desbordamiento horizontal ("Use standard `<table>` elements with `<thead>`, `<tbody>`, and `<th>` elements.").
- **`dialog`**: los diálogos de eliminación y restauración reutilizan el `<dialog>` nativo y `showModal()`, sin `role="dialog"`, `aria-modal` ni focus trap propio; el navegador aporta foco, capa superior y `Esc` ("Use the native `<dialog>` element for modal and non-modal dialogs. It provides the accessibility semantics, focus handling, top-layer rendering and `Esc` handling that custom overlays have to rebuild by hand."). Se añadieron reglas `dialog:modal { overscroll-behavior: contain; }` y `dialog::backdrop { overflow: hidden; overscroll-behavior: contain; }`.
- **`accessibility`**: el campo de confirmación y los botones nuevos conservan los estilos de foco globales basados en `:focus-visible`, sin reglas `:focus` que marquen el foco del ratón ("When defining custom focus indicators, always style the `:focus-visible` state (not `:focus`) explicitly.").

## Verificación

```powershell
dotnet build backend/DomiBase.sln
dotnet test  backend/DomiBase.sln --no-build
# 427/427: 272 unitarios, 120 integración, 35 seguridad

cd frontend/admin
npm.cmd test       # 42/42
npm.cmd run build
cd ..
node scripts/dev/verify-backups-panel.mjs   # 18/18
```

Cobertura nueva relevante:

- Unitarios: argumentos de `pg_dump`/`pg_restore` y redacción de credenciales (`PgToolArgumentsTests`), rutas GUID y rechazo de `..`/absolutas (`BackupFileStoreTests`), orquestación con herramientas falsas: lock, hash, auditoria, estados y limpieza temporal (`BackupServiceTests`).
- Integración real (`ProjectBackupTests`): backup → mutar datos → restore → verificar filas en PostgreSQL; aislamiento entre proyectos; dump corrupto rechazado antes de tocar la base; descarga y borrado.
- Seguridad (`BackupSecurityTests`): 401 sin token y con solo `apikey`, rutas Control inexistentes bajo `/rest/v1`, y respuestas sin token, pepper, clave AES ni `PGPASSWORD`.
- E2E (`scripts/dev/verify-backups-panel.mjs`): proyecto temporal con tabla y política RLS, fila insertada vía Runtime, creación y descarga reales desde el panel, mutación, confirmación por slug (incluido `Esc`), restauración que recupera la fila original, borrado, 390×844 sin desbordamiento, consola y respuestas limpias, y limpieza del proyecto. No imprime secretos ni contenido del volcado.

Capturas: `.impeccable/review/backups-desktop.png` y `.impeccable/review/backups-mobile.png`.

No se construyó ni publicó la imagen Docker de la API, no se hicieron commits/pushes y no se tocó el homelab.
