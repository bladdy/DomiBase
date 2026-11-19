# Verificación E2E del alcance mínimo

Registro ejecutable del criterio de aceptación: crear `paneles-solares`, provisionar su base, crear `contactos`, insertar y leer registros con el SDK, y demostrar que otro proyecto y un visitante sin permiso no pueden leerlos.

## Requisitos previos

- PostgreSQL 18 en Docker, healthy en `127.0.0.1:5433` (`docker compose up -d`).
- API ASP.NET Core en Windows con el `.env` local (`dotnet run --project backend/src/DomiBase.Api`).
- SDK compilado (`npm --prefix packages/client run build`).
- Para la prueba de Astro, ejemplo iniciado con `npm run dev --prefix examples/astro-example`.

Todas las verificaciones usan secretos del `.env` local sin imprimirlos. El verificador guarda solo correos y el slug en `scripts/dev/.minimal-e2e-state.json`; nunca escribe claves.

## Comando

```powershell
node scripts/dev/verify-minimal-admin.mjs
docker restart domibase-postgres
# esperar a que el health check vuelva a healthy y reiniciar la API para descartar conexiones pooled obsoletas
node scripts/dev/verify-minimal-admin.mjs --after-restart
node scripts/dev/phase-06-verify-astro.mjs http://localhost:4325
```

> Tras reiniciar PostgreSQL, las conexiones ya abiertas por Npgsql pueden quedar obsoletas (`SocketException 10053`). La persistencia se verifica reiniciando también la API; el objetivo de esta prueba es conservar datos, no calor de pool.

## Comprobaciones ejecutadas

| # | Comprobación | Resultado |
| --- | --- | --- |
| 1 | `POST /admin/v1/login` con usuario/contraseña del entorno; sesión `dbsess_` | OK |
| 2 | La sesión autoriza `GET /api/v1/projects` | OK |
| 3 | `paneles-solares` en estado `Ready`, base `domibase_paneles_solares` | OK |
| 4 | Tabla `contactos` y columnas `id, nombre, email, estado, created_at` desde `pg_catalog` | OK |
| 5 | Políticas `anon_select` y `anon_insert` presentes | OK |
| 6 | Emisión de clave pública `db_public_` (valor mostrado una sola vez) | OK |
| 7 | INSERT y SELECT desde `@domibase/client` con RLS real | OK |
| 8 | INSERT desde el Table Editor por `/api/v1/.../data/tables/.../rows` | OK |
| 9 | Persistencia de las filas tras `docker restart domibase-postgres` | OK (15/15 en la segunda pasada) |
| 10 | Clave inválida rechazada sin devolver datos | OK |
| 11 | Identificador con SQL injection rechazado por el SDK | OK |
| 12 | Tabla con RLS sin política no expone filas al visitante | OK |
| 13 | Otro proyecto con su propia clave no puede leer `contactos` | OK |
| 14 | Borrado controlado del proyecto temporal por slug | OK |
| 15 | Revocación de la clave de prueba | OK |

Resultado: **14/14** en la primera pasada y **15/15** tras el reinicio.

## Ejemplo Astro

`node scripts/dev/phase-06-verify-astro.mjs http://localhost:4325` validó:

- portada SSR y cabeceras de seguridad;
- INSERT desde el formulario con `@domibase/client`, visible en el listado;
- UPDATE y DELETE persistentes;
- diagnóstico en vivo: 7 comprobaciones en OK.

Si la clave del ejemplo queda revocada, se renueva con una nueva clave pública de `paneles-solares` desde la Control API y se actualiza `examples/astro-example/.env` (archivo local, no versionado).

## Hallazgo corregido durante la verificación

Crear `anon_insert` después de `anon_select` ejecutaba `REVOKE ALL` sobre el rol funcional y eliminaba el `GRANT SELECT` de la política previa. El Table Editor permitía marcar ambos interruptores pero la lectura se rompía. Ahora los `GRANT` son aditivos y limitados a los comandos de cada política; RLS sigue siendo la comprobación definitiva. Cubierto por `BuildStatements_ComplementaryPoliciesDoNotRevokeEachOther`.

## Verificación del panel del 2026-11-19

Con el stack local en marcha (PostgreSQL en Docker, API en `:5080` y `npm run dev` en `:5190`) se dirigió el panel con Chrome headless mediante el protocolo DevTools, sin instalar dependencias nuevas:

| Comprobación | Resultado |
| --- | --- |
| Proyectos usa una sola columna y no muestra sidebar (`1424px`) | OK |
| Table Editor muestra sidebar y contenido (`240px 1169px`) y el botón «Nueva tabla» | OK |
| Crear `contactos_ui_final(id int8 PK IDENTITY, nombre text)` desde el formulario | OK, sin errores |
| «Añadir columna» expone nombre, tipo, default y los cuatro marcadores; `IDENTITY` deshabilitado en `text` | OK |
| Añadir `email text` desde el panel | OK, visible en el esquema |
| Eliminación de la tabla de prueba por la Control API, dejando solo `contactos` | OK |

La causa del fallo reportado al crear tablas/columnas era una combinación inválida que el formulario permitía enviar (`IDENTITY` con `DEFAULT` o con un tipo no entero), rechazada por `SchemaDdlValidator`. La UI ya no genera esa petición; las reglas del servidor no cambiaron. Regresión cubierta por `frontend/admin/src/lib/schema-draft.test.ts` y `frontend/admin/src/styles.test.ts`.
