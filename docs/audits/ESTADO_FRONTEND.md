# Estado funcional del frontend — inventario de capacidades

**Fecha:** 2026-11-19  
**Alcance:** auditoría solo lectura de lo que el propietario puede hacer hoy en `frontend/admin` y `examples/astro-example`. No modifica código, contratos ni servicios.  
**Fuentes inspeccionadas:** `frontend/admin/src/App.tsx`, componentes bajo `frontend/admin/src/components/`, `frontend/admin/src/lib/control-client.ts`, `packages/client/src/index.ts` y páginas de `examples/astro-example/src/pages/`.

## 1. Superficie disponible

| Servicio | URL | Función |
| --- | --- | --- |
| Panel privado | `http://127.0.0.1:5190` | Control Plane del propietario |
| API | `http://127.0.0.1:5080` | Control (`/api/v1`, `/admin/v1`) y Runtime (`/rest/v1`) |
| Ejemplo Astro | `http://localhost:4325` | Consumo real del SDK desde SSR |

El panel usa rutas relativas: Vite proxya `/api` y `/admin` hacia `CONTROL_API_TARGET` (`http://127.0.0.1:5080` por defecto). La URL de la API no se incrusta en el bundle. Sin el proxy `/admin`, el login caería en el servidor del panel en `:5190`.

## 2. Panel admin

Flujo vigente: **Login → Proyectos → Resumen / Table Editor / Conexión → SDK**.

### 2.1 Acceso y sesión
- Login con `DOMIBASE_ADMIN_USERNAME` / `DOMIBASE_ADMIN_PASSWORD` del `.env` local.
- `POST /admin/v1/login` devuelve una sesión `dbsess_` firmada y caducable.
- La sesión se guarda solo en `sessionStorage` de la pestaña; `DOMIBASE_ADMIN_TOKEN` no se usa en el panel.
- Cerrar sesión con confirmación; un 401 / `UNAUTHORIZED` / `NO_SESSION` fuerza salida y limpia el estado.
- La barra superior muestra la ruta actual (`Proyectos / <slug> / <sección>`).

### 2.2 Proyectos (navegación global)
- Listar proyectos con estado real de provisioning y base asignada.
- Crear proyecto con slug y nombre; la base PostgreSQL independiente se aprovisiona automáticamente.
- Reintentar provisioning cuando el estado es `Failed`.
- Editar el cupo de rate limit (peticiones y ventana en segundos; campos vacíos = heredar el default).
- Eliminar proyecto con confirmación por **slug exacto**: borra metadatos y base de datos.

### 2.3 Resumen
- Nombre, slug, estado, base de datos, fecha de creación y límite efectivo de peticiones.
- Accesos directos a «Abrir Table Editor» y «Ver conexión».
- Si hay `failureReason`, se muestra el último error de provisioning.
- Botón «Reintentar provisioning» solo en estado `Failed`; botón «Eliminar proyecto».

### 2.4 Table Editor
Escritura mediante Control Plane con operaciones tipadas y validadas; la clave pública no se usa para administrar el esquema.

- **Tablas:** crear con una o varias columnas; seleccionar tabla; eliminar tabla con confirmación.
- **Columnas:** nombre, tipo de la allowlist (`uuid`, `text`, `varchar(255)`, `int4`, `int8`, `boolean`, `timestamptz`, `date`, `jsonb`, `numeric`, `bytea`, `inet`), valor por defecto y marcadores `nullable`, `primaryKey`, `unique`, `identity`. `IDENTITY` solo aplica a tipos enteros y no admite `DEFAULT`.
- **Visualización:** tipo, nulabilidad, default, `PK` / `IDENTITY` / `UNIQUE` y referencias FK.
- **RLS mínimo:** interruptores para `anon_select` y `anon_insert`, con expresiones fijas `true` y `GRANT` aditivos por comando. Sin política, el SDK no puede leer ni escribir.
- **Registros:** filtro por columna con `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `is`; orden ascendente/descendente; límite 1–200; paginación «Anterior» / «Siguiente»; se muestra el rol y el número de filas.
- **Cambios:** insertar una o varias filas desde JSON; actualizar y eliminar con filtro acotado obligatorio (p. ej. `id.eq.1`).

### 2.5 Conexión
- URL de la API y ruta `/rest/v1/<tabla>`; ambos copiables.
- Crear clave pública `db_public_`: el valor completo se muestra **una sola vez** y se puede copiar.
- Listado de claves públicas activas (prefijo, etiqueta, fecha de creación).
- Información PostgreSQL permitida: nombre de la base, esquema `public`, roles `anon` y `authenticated`. Nunca credenciales administrativas ni conexión privilegiada.
- Configuración JSON del SDK y ejemplo de uso con `select(...).order(...).limit(...)`, ambos copiables.

## 3. Ejemplo Astro

| Página | Papel |
| --- | --- |
| `/` | Alta de contactos con el SDK propio desde SSR; flujo verificado E2E (INSERT / UPDATE / DELETE) |
| `/diagnostico` | Clave inválida, clave revocada, tabla no expuesta y contexto RLS en vivo |
| `/almacen`, `/notas`, `/login`, `/funciones`, `/tiempo-real` | Páginas heredadas de la demo; **no forman parte del producto** |

Storage, Auth de usuarios finales, Functions y Realtime quedaron fuera del alcance y no están en los exports activos de `@domibase/client`, que se limita a `createClient`, `DomiBaseClient`, `DomiBaseError`, `QueryBuilder` y los tipos públicos de fase 1.

## 4. Fuera de la interfaz

Auth de usuarios finales, Storage, Realtime, Functions, SQL Editor, Migraciones, Extensiones, Backups y Auditoría; organizaciones, facturación, planes y clientes SaaS. Su código backend/shared se conserva como registro histórico, pero no se ofrece en el panel ni en el SDK documentado.

## 5. Hallazgos y limitaciones

1. **La rotación de clave pública no revoca la anterior.** `ConnectionSection` llama a `createKey` con etiqueta `panel` bajo el rótulo «Rotar clave pública». El cliente expone `revokeKey` y existe `frontend/admin/src/components/KeysSection.tsx` con la UI de revocación, pero ese componente **no está montado** en `App.tsx`. La clave antigua sigue siendo válida hasta revocarla por API.
2. **Sin FK desde la UI:** las foreign keys se muestran en el esquema, pero no se pueden crear desde el panel.
3. **Sin renombrado:** no hay operación para renombrar tablas ni columnas.
4. **Operación tras reiniciar PostgreSQL:** reiniciar también la API para descartar conexiones pooled obsoletas; la persistencia quedó demostrada con ese procedimiento.

## 6. Evidencia de verificación previa

- Panel: `npm run check` limpio, `npm test` 57/57, `npm run build` correcto.
- Backend: 491/491 tests (311 unitarios, 134 integración, 46 seguridad).
- SDK: 70/70 tests.
- E2E del flujo mínimo: 14/14 antes del reinicio y 15/15 tras `docker restart domibase-postgres` + reinicio de API.
- Login a través del proxy: `POST http://127.0.0.1:5190/admin/v1/login` → HTTP 200 con sesión emitida.
- Detalle ejecutable: `docs/implementation/admin-minimal-e2e.md`; alcance y contratos: `docs/implementation/admin-minimal-scope.md`.

## 7. Conclusión

El panel cubre de extremo a extremo el flujo mínimo acordado: login, proyectos con PostgreSQL propio, tablas/campos/registros y conexión con SDK. No se construyó ni publicó imagen Docker, no se tocó el homelab y no se hicieron commits ni pushes.
