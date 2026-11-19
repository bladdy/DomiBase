# DomiBase Admin

Panel privado del Control Plane. React + Vite + TypeScript, sin dependencias de Supabase y sin secretos en el bundle.

## Arranque

Requiere Node ≥ 22.23.4, PostgreSQL en Docker (`127.0.0.1:5433`) y la API escuchando en `http://127.0.0.1:5080`.

```powershell
cd frontend/admin
cmd /c npm install
cmd /c npm run dev
```

Abre `http://127.0.0.1:5190` e introduce el usuario y la contraseña configurados en `DOMIBASE_ADMIN_USERNAME` / `DOMIBASE_ADMIN_PASSWORD` (`.env` local de la API). El servidor devuelve una sesión `dbsess_` firmada y caducable, guardada solo en `sessionStorage`. El token permanente `DOMIBASE_ADMIN_TOKEN` no se usa en el panel.

Para apuntar a otra API, crea `frontend/admin/.env.local` (no versionado) con:

```dotenv
CONTROL_API_TARGET=http://127.0.0.1:5080
```

Vite proxya `/api` y `/admin` hacia ese destino (el login del propietario vive en `/admin/v1/login`), así que la URL nunca se incrusta en el cliente y no hace falta configurar CORS.

## Navegación

- **Proyectos** (global): crear proyecto, abrirlo, editar su cupo y eliminarlo con confirmación por slug.
- Dentro de un proyecto: **Resumen**, **Table Editor**, **Conexión** y **Volver a proyectos**.

No hay pestañas de Auth, Storage, Realtime, Functions, SQL, Migraciones, Extensiones, Backups ni Auditoría.

## Qué se puede hacer

- **Resumen:** estado de provisioning, base de datos asignada, fecha, cupo efectivo, reintento cuando el estado es `Failed` y borrado controlado.
- **Table Editor:**
  - crear y eliminar tablas con confirmación;
  - definir columnas con tipos PostgreSQL allowlisted, `NOT NULL`, PK, UNIQUE, DEFAULT y `IDENTITY` sobre tipos enteros;
  - ver tipos, nulos, defaults y claves desde el catálogo real; añadir y eliminar columnas;
  - activar solo las políticas RLS necesarias (`SELECT` y/o `INSERT` para `anon`);
  - explorar registros con filtros, orden, límite y paginación, e insertar/actualizar/eliminar filas con filtros acotados.
- **Conexión:** URL de la API, clave pública (el valor completo se muestra una sola vez al crearla/rotarla), base y roles no secretos, configuración JSON y ejemplo del SDK.

Las escrituras de registros se ejecutan mediante la Control Plane reutilizando la ruta Runtime con el rol `anon`, RLS y GRANT reales. El panel nunca envía SQL, nombre de base, rol PostgreSQL ni la clave pública al servidor para consultar datos.

## Comandos

| Comando | Qué hace |
| --- | --- |
| `cmd /c npm run dev` | Servidor de desarrollo en `127.0.0.1:5190` |
| `cmd /c npm run check` | Comprobación de tipos (sin emitir) |
| `cmd /c npm test` | Tests del cliente HTTP con `fetch` inyectable |
| `cmd /c npm run build` | Typecheck + build de producción con CSP |
| `cmd /c npm run preview` | Sirve el build con el mismo proxy `/api` y `/admin` |

## Seguridad

- La sesión se envía como `X-DomiBase-Admin-Token` únicamente desde `src/lib/control-client.ts`.
- Nunca se escribe en `localStorage`, URLs, logs ni código fuente. Cerrar la pestaña la elimina.
- La clave recién emitida vive solo en memoria de React; no se persiste.
- Las acciones destructivas usan un `<dialog>` nativo con confirmación y foco confinado; el borrado de proyecto exige escribir el slug exacto.
- El build inyecta una CSP estricta en `index.html`. `frame-ancestors` debe añadirse como cabecera HTTP en el despliegue real.

## Estructura

```
src/
  App.tsx                         sesión, proyectos y cambio de sección
  components/TabRail.tsx          navegación de proyecto (3 secciones + volver)
  components/ProjectsSection.tsx  proyectos, cupo y acceso
  components/ProjectOverviewSection.tsx
  components/TableEditorSection.tsx
  components/ConnectionSection.tsx
  components/LoginGate.tsx        usuario/contraseña
  components/ConfirmDialog.tsx
  lib/control-client.ts           HTTP + tipos + errores canónicos
  lib/control-client.test.ts      tests con fetch inyectable
  lib/session.ts                  sessionStorage de la sesión
  lib/format.ts                   fechas e identificadores
  styles.css                      sistema visual de consola
```

Detalle de decisiones y verificación: `docs/implementation/admin-minimal-scope.md`.
