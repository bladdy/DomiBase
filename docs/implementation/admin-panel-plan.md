# Fase 6 (post-MVP) — Panel privado de administración

## Estado

Completada y verificada localmente. Es la primera entrega posterior al MVP funcional de Fases 1–5. El prefijo “Fase 6” ya se usa para el ejemplo Astro dentro del MVP; en la documentación se distingue como **Fase 6 — Panel privado**.

> Ampliaciones posteriores: las entregas P1 ampliaron el panel con esquema, SQL, extensiones, migraciones, cupo, backups y auditoría. P2.1 añadió el explorador de filas con RLS real y el editor de políticas RLS allowlisted (`p2-1-data-explorer-rls.md`); por eso el alcance original “sin tablas/políticas” debe leerse como instantáneo de Fase 6.
>
> **Estado actual:** este documento es histórico. El panel se ha simplificado a la superficie mínima acordada (login → Proyectos → Resumen/Table Editor/Conexión). Las decisiones vigentes, contratos y verificación están en `docs/implementation/admin-minimal-scope.md`. Las referencias a login por token pegado y a las pestañas retiradas no deben aplicarse.

## Decisiones cerradas

- **Ubicación:** `frontend/admin` (React + Vite + TypeScript estricto). No se crean proyectos vacíos adicionales.
- **Alcance:** MVP sobre la Control API existente:
  - listar y crear proyectos;
  - ver estado, base asignada, fecha y motivo de fallo de provisioning;
  - reintentar provisioning de proyectos en `Failed`;
  - listar, emitir y revocar API keys (el valor completo se muestra **una sola vez**, en memoria);
  - leer buckets y funciones del proyecto seleccionado.
- **Fuera de alcance:** crear tablas/columnas/políticas RLS desde el panel. El backend aún no expone esos endpoints y `AGENTS.md` los difiere a un incremento posterior. El contrato de Storage/Functions se consume solo en modo lectura.
- **Autenticación:** el propietario introduce `DOMIBASE_ADMIN_TOKEN`. Se valida contra `GET /api/v1/projects` antes de aceptarlo. Se guarda únicamente en `sessionStorage` (`domibase.admin.token`) y se envía como `X-DomiBase-Admin-Token`. No hay nuevo endpoint de login ni cookies; CORS no se usa como autorización.
- **Origen:** Vite sirve el panel en `127.0.0.1:5190` y proxya `/api` y `/admin` a `http://127.0.0.1:5080`. La URL de la API no se incrusta en el bundle; se puede cambiar con `CONTROL_API_TARGET` (.env local de Vite).
- **Dirección visual:** manual de operaciones encuadernado, con raíl de pestañas por división, tablero de color por sección y hojas de acetato. Registrada en `.impeccable/surfaces/frontend-admin.md` junto al contrato de dirección; el sistema visual y su procedencia se documentan en `DESIGN.md` con el sidecar `.impeccable/design.json`.
- **Build path:** code-first (decisión del propietario), sin comps generados. Verificación programática con Chrome headless en escritorio y móvil.

## Contratos consumidos

Todos bajo `/api/v1` y protegidos por el middleware administrativo:

| Operación | Endpoint | Uso en el panel |
| --- | --- | --- |
| Listar proyectos | `GET /projects` | Validación de sesión y tabla de proyectos |
| Crear proyecto | `POST /projects` | Formulario “Nuevo proyecto” |
| Reintentar provisioning | `POST /projects/{id}/provision` | Acción en filas `Failed` |
| Listar claves | `GET /projects/{id}/keys` | Tabla de claves (metadatos sanitizados) |
| Emitir clave | `POST /projects/{id}/keys` | Muestra el valor completo una vez |
| Revocar clave | `DELETE /projects/{id}/keys/{keyId}` | Confirmación explícita |
| Listar buckets | `GET /projects/{id}/buckets` | Lectura |
| Listar funciones | `GET /projects/{id}/functions` | Lectura con límites y hash |
| Listar tablas del esquema | `GET /projects/{id}/schema/tables` | Pestaña Esquema, introspección en vivo |
| Listar vistas del esquema | `GET /projects/{id}/schema/views` | Pestaña Esquema, introspección en vivo |
| Listar funciones del esquema | `GET /projects/{id}/schema/functions` | Pestaña Esquema, introspección en vivo |

El error canónico `{ error: { code, message, details?, hint? } }` se traduce a `ControlApiError` y se muestra sin exponer secretos, SQL ni trazas.

## Seguridad

- El token nunca se escribe en `localStorage`, logs, URL, título ni bundle. Solo vive en `sessionStorage` y en la memoria del proceso del navegador mientras la pestaña esté abierta.
- `ControlClient` es el único punto que añade la cabecera administrativa; `fetch` es inyectable para tests.
- La clave recién creada se conserva solo en estado de React; al cambiar de sección o cerrar la pestaña desaparece.
- “Revocar clave” y “Cerrar sesión” usan un `<dialog>` nativo con foco confinado y confirmación explícita.
- El build de producción inyecta CSP: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'self'; object-src 'none'`. `frame-ancestors` se omite en `<meta>` porque los navegadores lo ignoran; debe emitirse como cabecera HTTP en el despliegue real. Configuración de referencia para Nginx (documental, **no aplicada** al homelab):

  ```nginx
  add_header Content-Security-Policy "frame-ancestors 'none';" always;
  ```

  Con esta plantilla la cabecera deja de ser una intención implícita: queda especificada en el repositorio y se instalará solo cuando se autorice el despliegue.
- `npm audit` informa de un aviso upstream en `picomatch <= 4.0.7` (dependencia de herramientas de desarrollo Vite/Vitest) sin versión corregida publicada. No afecta al bundle de producción ni al Runtime API.

## Extensión — inspector de esquema

Pestaña **Esquema** añadida sobre la misma sesión administrativa. Muestra tablas, vistas (incluidas materializadas) y funciones SQL del esquema `public` de la base del proyecto seleccionado, leyendo `pg_catalog` en vivo desde la Control Plane. No hay controles de escritura, ni columnas, índices, políticas RLS o DDL.

Decisiones de seguridad: el esquema es una constante del servidor; el cliente solo envía el `projectId` de la ruta; se valida el nombre de base contra `^domibase_[a-z0-9_]+$`; el proyecto debe estar `Ready`; la conexión usa la cadena privilegiada de provisioning con transacción `READ ONLY` y `ApplicationName = domibase-catalog`; cada lectura se audita como `schema.tables`, `schema.views` o `schema.functions`. Las claves públicas del Runtime y los tokens inválidos no pueden invocar estas rutas. Detalle completo en `docs/implementation/schema-inspection.md`.

## Extensión — SQL Editor y catálogo de extensiones

La pestaña **SQL** envía una única sentencia de solo lectura a `POST /api/v1/projects/{id}/sql/query`; el servidor valida, acota filas y timeout, fuerza `READ ONLY` y audita un fingerprint sin la sentencia. La pestaña **Extensiones** consume `GET/POST/DELETE /api/v1/projects/{id}/extensions` sobre un catálogo allowlisted de diez extensiones `trusted`: el cliente solo envía el nombre, el servidor construye el DDL idempotente con `SET LOCAL ROLE` al owner y registra `extension.list/install/uninstall`. Ninguna de las dos rutas existe bajo `/rest/v1`; la desinstalación usa el mismo `<dialog>` nativo de confirmación. Detalle completo en `docs/implementation/p1-2-sql-editor.md` y `docs/implementation/p1-5-extensions.md`.

## Extensión — Migraciones versionadas

La pestaña **Migraciones** consume `GET/POST /api/v1/projects/{id}/migrations` sin cambiar el contrato de P1.3. `MigrationSection` muestra el historial (versión, nombre, operaciones, checksum y fecha), ofrece un constructor de planes con ocho operaciones tipadas y presenta el JSON resultante en una vista previa de solo lectura antes de abrir el `<dialog>` nativo de confirmación. El módulo `migration-plan.ts` replica en el cliente las allowlists del servidor: identificadores `^[a-z_][a-z0-9_]*$` (máx. 63, sin prefijo `pg_`), tipos SQL permitidos y `varchar(n)` acotado, literales por defecto, comandos/roles de política y las tres expresiones de política seguras. La validación es una ayuda de interfaz; la autoridad sigue siendo `SchemaDdlValidator` y `MigrationService`.

No se añade SQL arbitrario ni editor libre. `add_column` y `alter_column` quedan fuera del constructor porque `SchemaRequestParser` usa `type` tanto para el discriminador de operación como para el tipo SQL/cambio de columna; ofrecerlos en el panel exigiría cambiar el contrato. La limitación se documenta en la propia pestaña y en `frontend/admin/README.md`. Cada cambio de campo invalida el plan preparado, de modo que no se puede aplicar una vista previa obsoleta.

## Verificación

- `npm.cmd run check` — TypeScript estricto, sin errores.
- `npm.cmd test` — 14 tests del cliente HTTP (cabecera, errores, 204, AbortSignal, red, JSON inválido, ausencia de token, ciclo de sesión y las tres rutas de esquema).
- `npm.cmd run build` — build de producción con CSP inyectado.
- Backend: build limpio y 182 tests en verde (109 unitarios, 64 de integración y 9 de seguridad), incluidos los nuevos tests de introspección, auditoría y rechazo de credenciales.
- Playwright/Chrome headless sobre el build: **20 comprobaciones en verde**, sin errores de consola. Cubren pestañas y orientación reactiva, sesión conservada tras recarga, un solo `h1` y sin kicker, cambio de panel, clave revocada, emisión de clave y no filtrado al cambiar de proyecto, IDs únicos de diálogo, sin desbordamiento en 1440×900 y 390×844, y tabla con semántica preservada y desplazamiento horizontal en móvil. Capturas en `.impeccable/review/`.
- Verificación adicional de la pestaña Esquema con Chrome headless + CDP contra la API real: pestaña activa, panel visible, un solo `h1` (“Esquema”), subtítulo `paneles-solares · esquema public`, tres agrupaciones, fila real `contactos`, sin desbordamiento en 1440×900 ni 390×844 y **cero errores de consola / respuestas 4xx**. Se añadió un favicon en línea para eliminar el único `404` de `/favicon.ico`. Capturas: `.impeccable/review/schema-desktop.png` y `schema-mobile.png`.
- `impeccable detect --json --target frontend/admin` — sin hallazgos.
- Verificación de la extensión de migraciones: **34/34 tests del panel** (22 preexistentes + 3 de cliente + 9 del planificador) y build de producción correcto.
- Verificación E2E de la pestaña: `node scripts/dev/verify-migrations-panel.mjs` — **15/15 comprobaciones en verde** con Chrome headless contra la API real. Cubre sesión, foco visible, plan `create_table` + `create_policy`, vista previa sin SQL, aplicación, historial/checksum, idempotencia, conflicto, tabla semántica, sin desbordamiento en 390×844, consola/respuestas limpias y limpieza del proyecto temporal. Capturas en `.impeccable/review/migrations-*.png`. El hallazgo responsive (642 px de desbordamiento) se corrigió en origen con `minmax(0, 1fr)` y `flex-wrap`, no con `overflow-x: hidden`.
- Estado acumulado al cierre de migraciones: **367/367 tests backend** y **34/34 tests del panel**; build de producción correcto. Las cifras de arriba corresponden al cierre de la Fase 6 / inspector de esquema.
- Estado al cerrar P1.4: **383/383 tests backend** y **35/35 tests del panel** (instantánea histórica; el total vigente está en `docs/implementation/roadmap.md`); la pestaña Proyectos incluye la columna **Cupo** y su edición con `<dialog>` nativo (`docs/implementation/p1-4-rate-limits.md`).

## Arranque local

```powershell
# Terminal 1: API (necesita Docker con PostgreSQL en 127.0.0.1:5433)
dotnet run --project backend/src/DomiBase.Api

# Terminal 2: panel
cd frontend/admin
cmd /c npm install
cmd /c npm run dev        # http://127.0.0.1:5190
```

Introduce el mismo `DOMIBASE_ADMIN_TOKEN` del `.env` local. Pasos detallados en `frontend/admin/README.md` y `docs/development/local-setup.md`.
