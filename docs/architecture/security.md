# Seguridad de DomiBase

## Principios obligatorios
- DomiBase es una implementación propia: sin GoTrue, sin PostgREST, sin Supabase SDK y sin código de Supabase.
- **RLS y GRANT** son mecanismos obligatorios para el acceso a datos de proyectos.
- La base `domibase_control` **nunca** es accesible desde el SDK público ni desde claves públicas del Runtime.
- El aislamiento de red no reemplaza el aislamiento de credenciales y permisos.

## Límites de confianza
1. Panel privado y Control API (administración, solo propietario).
2. Runtime público (API de datos para aplicaciones).
3. Base de control (`domibase_control`).
4. Base de datos de cada proyecto.
5. Aplicaciones consumidoras (SDK `@domibase/client`).

Aunque Control y Runtime compartan proceso en Fase 1, sus endpoints, autenticación, servicios y conexiones están separados.

## API keys
- `db_public_` identifica el proyecto y puede aparecer en el frontend; **no concede permisos por sí sola**.
- `db_secret_` puede emitirse desde P0.3, pero el Runtime la rechaza siempre: no eleva privilegios ni selecciona otro rol PostgreSQL.
- **Generación criptográfica:** prefijo `db_public_`/`db_secret_` + 32 bytes de `RandomNumberGenerator` en base64url.
- **Almacenamiento:** solo se guarda `HMAC-SHA-256(pepper, valor)` en hexadecimal (64 caracteres), único. Desde P0.4 el pepper activo es el primero de `DOMIBASE_API_KEY_PEPPERS` (mínimo 16 caracteres, obligatorio al arrancar); el resto del keyring y la variable histórica `DOMIBASE_API_KEY_PEPPER` solo verifican claves antiguas. **Nunca** se guarda el valor original.
- **Exposición:** el valor completo se devuelve solo al crear o rotar. El listado devuelve únicamente metadatos (prefijo + 8 caracteres, etiqueta, fechas); nunca el valor ni el hash.
- **Ciclo de vida:** revocación idempotente, expiración server-side y rotación con solapamiento configurable (0–1440 minutos, por defecto 60) mediante `POST /api/v1/projects/{id}/keys/{keyId}/rotate`. El valor anterior sigue resolviendo solo durante la ventana; la fila antigua queda revocada de inmediato.
- Cambiar el pepper ya no invalida los hashes existentes: `IApiKeyGenerator.Verify` compara en tiempo constante contra todo el keyring y el Runtime genera todos los hashes candidatos en servidor. Ver `docs/implementation/p0-4-secrets.md`.

## PostgreSQL y aislamiento por proyecto
- Un servidor PostgreSQL compartido con **una base independiente por proyecto** (`domibase_<slug>`, guiones → guiones bajos).
- **Roles separados:** `<db>_owner` (propietario/migraciones) distinto de `<db>_runtime` (login técnico del Runtime) y `<db>_anon` (rol funcional de solicitudes anónimas).
- En Fase 4: `_owner` y `_anon` son `NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`. `_runtime` es **`LOGIN NOINHERIT`** con los mismos límites, miembro de `_anon` para poder hacer `SET ROLE` dentro de la transacción.
- La contraseña de `_runtime` se genera con 32 bytes aleatorios (base64url) y se cifra en `domibase_control` con **AES-256-GCM**. Desde P0.4 se usa el keyring `DOMIBASE_SECRETS_KEYS` (`id:base64-clave`, formato `v2.<id>.<payload>`), y `DOMIBASE_SECRETS_KEY` se conserva solo para descifrar valores `v1`. Nunca se almacena en claro ni se devuelve por la API.
- **El rol Runtime no puede omitir RLS:** RLS habilitado en tablas expuestas y políticas específicas por caso.
- **GRANT mínimos** por tabla y operación.
- Rol `authenticated` reservado para fase posterior.
- La base se crea con `OWNER` el rol owner, `UTF8` y `TEMPLATE template0`. Se revoca `CONNECT` y `CREATE` a `PUBLIC`; runtime/anon solo reciben `CONNECT` y `USAGE` sobre `public`.
- La conexión administrativa/privilegiada queda restringida al proceso de provisioning. El rol administrativo local es superuser; en despliegue debe reducirse a `CREATEDB`/`CREATEROLE` y acceso a `domibase_control`.

## Contexto de usuario/claims por transacción
- El Runtime ejecuta transacciones cortas y aplica contexto **local a la transacción**.
- Implementado en Fase 4: `set_config('role', '<slug>_anon', true)`, `search_path=public`, `request.jwt.claims` con `{"role":"anon","project_id":"...","api_key_id":"..."}`, más `domibase.project_id` y `domibase.api_key_id`.
- La identidad/claims se derivan de credenciales verificadas por el servidor; **nunca** se confían roles o project IDs enviados por el cliente.
- El contexto desaparece al terminar la transacción. Un test reutiliza una conexión pooled y comprueba que ni el rol ni el claim persisten.

## SQL seguro
- Valores siempre parametrizados.
- Identificadores de tablas/columnas validados contra metadatos/allowlist y quoted correctamente.
- Nunca aceptar fragmentos SQL arbitrarios desde el SDK ni endpoints públicos de SQL.
- `PATCH`/`DELETE` requieren filtros acotados y validados.

### DDL administrativo (P1.1)
- La Control API nunca acepta SQL arbitrario: el cliente envía una operación tipada y el servidor construye el DDL.
- Identificadores `[a-z_][a-z0-9_]*` (≤63 caracteres, sin prefijo `pg_`); tipos allowlisted; defaults literales con escape y solo las expresiones fijas `gen_random_uuid()` y `now()`.
- Expresiones de política restringidas a `true`, `false` y `project_id = current_setting('domibase.project_id', true)::uuid`; cualquier otro texto se rechaza.
- Los nombres PostgreSQL físicos se derivan del proyecto; el cliente solo envía los roles lógicos `anon`/`authenticated`.
- Cada DDL corre en una transacción corta con `SET LOCAL ROLE` al owner. Las tablas nuevas nacen con `ENABLE ROW LEVEL SECURITY`: sin GRANT ni política no hay acceso, ni siquiera con una API key válida.
- Cada política crea un `GRANT` **aditivo** limitado a sus comandos; no se revocan los `GRANT` de políticas complementarias. RLS sigue siendo la comprobación definitiva: sin política no hay filas aunque exista `GRANT`, y sin `GRANT` no hay acceso aunque exista política. El panel mínimo solo gestiona `anon_select` y `anon_insert` con expresión fija `true`.
- Los errores PostgreSQL se traducen a mensajes seguros; el SQLSTATE solo se conserva como `hint`. Toda operación correcta se audita sin SQL ni secretos.

### Migraciones versionadas (P1.3)
- La Control API nunca acepta SQL arbitrario en una migración: el cliente envía un lote de operaciones tipadas y el servidor reutiliza el validador y el generador DDL de P1.1.
- El historial `domibase_migrations.applied` vive en cada base de proyecto, en un esquema privado que el inspector (`public`) y el Runtime (allowlists de `public`) no exponen.
- Cada versión es inmutable: nombre y operaciones se validan, y un checksum `v1.<SHA-256>` determinista impide redefinir una versión ya aplicada (`409 CONFLICT`).
- Las versiones se aplican en orden creciente; el DDL y la fila de historial se confirman en una sola transacción con `SET LOCAL ROLE` al owner y un advisory lock por base para serialize concurrencia.
- Reaplicar una versión idéntica es un no-op auditado (`migration.reapplied`); no se ejecuta DDL ni se duplica la fila.

### SQL Editor administrativo (P1.2)
- Es la única superficie que acepta texto SQL, y sigue siendo administrativa: `POST /api/v1/projects/{id}/sql/query`, protegida por el token de la Control Plane. Nunca existe bajo `/rest/v1` ni para claves públicas.
- El validador léxico blanquea literales, identificadores entre comillas, comentarios y dollar-quotes antes de aceptar una única sentencia `SELECT`/`WITH`. Bloquea DML/DDL, control de transacciones, `INTO`, funciones de escritura y funciones de sistema peligrosas.
- PostgreSQL aplica la defensa definitiva: `SET TRANSACTION READ ONLY`, `SET LOCAL ROLE` al owner del proyecto y `statement_timeout` local a la transacción. El cliente no envía roles ni `search_path`.
- El resultado se capsula con `to_jsonb` y un límite parametrizado (`maxRows`, 1–1.000); una fila extra revela truncamiento sin consultas adicionales.
- Los errores PostgreSQL se traducen a mensajes fijos. Los errores de sintaxis **no devuelven** `MessageText`, para no reflejar fragmentos de SQL. Ninguna respuesta contiene SQL interno, credenciales ni nombres de roles físicos.
- La auditoría registra `sql.query` con base, filas, truncamiento, duración, límites y `sqlFingerprint = sha256:<16 hex>`; **la sentencia nunca se persiste**.

## Runtime
- Resolución server-side por hash de API key pública; un proyecto no `Ready` no sirve datos y las credenciales de Runtime inservibles responden 503.
- Solo se exponen tablas `public` con RLS habilitado y algún privilegio; los privilegios por columna se leen del catálogo bajo el rol funcional.
- Rate limiting **por proyecto** con ventana fija en el único host (P1.4): `projects.rate_limit_permits` / `projects.rate_limit_window_seconds` (1–10000 y 1–3600; `NULL` hereda `RUNTIME_RATE_LIMIT_PERMITS`=300 / `RUNTIME_RATE_LIMIT_WINDOW_SECONDS`=60). La partición es `project:{id}:{permits}:{window}` y la comparten todas las claves del proyecto; claves inválidas o ausentes caen a `ip:{dirección}` con defaults. Cubre `/rest/v1`, `/storage/v1`, `/functions/v1`, `/auth/v1` y el handshake de `/realtime/v1` (no frames WebSocket). Rechazo **429 `RATE_LIMITED`**; la clave API nunca entra en la partición ni en logs. Sin Redis: varias réplicas requerirían un almacén compartido.
- Límites de tamaño de body (`RUNTIME_MAX_BODY_BYTES`, 1 MiB) y de filas por inserción (`RUNTIME_MAX_INSERT_ROWS`, 100).
- CORS por proyecto como control complementario, no como autorización.
- Errores sin stack traces ni secretos/SQL interno; mensajes 5xx genéricos.
- Logs estructurados con tokens, contraseñas y API keys redactados.
- La contraseña de Runtime solo se descifra en memoria para abrir la conexión; no se registra ni se expone.

## Control API
- Autenticación administrativa independiente: el panel usa `POST /admin/v1/login` con usuario/contraseña del entorno y una sesión `dbsess_` HMAC-SHA-256 caducable enviada en `X-DomiBase-Admin-Token`; `DOMIBASE_ADMIN_TOKEN` queda reservado a servicios y tests. Autorización en cada operación.
- No exponer la base de control ni sus credenciales a Runtime público.
- Auditoría de acciones administrativas y errores de provisioning.
- Backups P1.6: cinco rutas bajo `/api/v1/projects/{id}/backups`, sin cuerpo para crear/borrar, con `X-DomiBase-Confirm: <slug>` para restaurar; nunca bajo `/rest/v1`. El solicitante no elige SQL, base, rol, ruta de archivo ni credenciales: todo se deriva de metadatos del servidor. El hash SHA-256 se verifica antes de la restauración y el archivo se guarda bajo GUIDs del servidor con escritura atómica. La contraseña de provisioning solo se pasa por `PGPASSWORD` al proceso hijo; `argv`, logs y auditoría no la contienen.
- Auditoría P1.7: dos lecturas solo administrativas, `GET /api/v1/audit` (filtros allowlistados, paginación acotada y `details` redactado) y `GET /api/v1/projects/{id}/provisioning-operations` (pasos del proyecto resuelto por metadatos). La lectura no escribe auditoría, no acepta SQL ni rutas y no existe bajo `/rest/v1`.
- Implementado en Fase 3: `project.create`, `project.provisioned`, `project.provision_failed`, `api_key.create`, `api_key.revoke`, más un registro por paso en `provisioning_operations`. Desde P1.1: `schema.table.*`, `schema.column.*`, `schema.index.*`, `schema.policy.*` y `schema.policies`, con metadatos no secretos (base, esquema, tabla y objeto). Desde P1.3: `migration.applied`, `migration.reapplied` y `migration.list` (base, versión, nombre, checksum y número de operaciones). Desde P1.2: `sql.query` (base, filas, truncamiento, duración, límites y fingerprint; nunca la sentencia). Desde P2.1: `data.preview` (base, rol `anon`, filas, truncamiento, límites y número de filtros; nunca filtros literales ni SQL). Desde P1.4: `project.rate_limit.updated` (slug y valores de cupo; sin claves ni secretos). Desde P1.6: `project.backup.created`, `.failed`, `.deleted`, `.restore_started`, `.restored` y `.restore_failed` (-ID de copia, formato, tamaño y estado; nunca rutas, roles ni secretos).

## Pruebas de seguridad
Probar claves inválidas/revocadas, aislamiento entre proyectos, RLS (denegación/permiso según política), SQL injection en valores e identificadores, acceso a tablas internas/no expuestas, escalamiento de roles y no contaminación del contexto en conexiones pooled.

La verificación vigente del flujo mínimo está en `docs/implementation/admin-minimal-e2e.md`: 14/14 comprobaciones antes del reinicio y 15/15 tras reiniciar PostgreSQL, incluidos RLS sin política, clave inválida, inyección en identificador, proyecto ajeno y persistencia de filas. El ejemplo Astro valida además INSERT/UPDATE/DELETE reales con el SDK propio.

Cobertura acumulada (**491 tests backend**; 46 de seguridad dedicados más los casos de integración): clave ausente/inválida/revocada/`secret`, expiración y solapamiento de rotación, tabla sin `GRANT`, tabla sin RLS, RLS sin política (SELECT vacío, INSERT 403), aislamiento de filas por claim `domibase.project_id`, aislamiento entre dos proyectos reales, inyección SQL en valores/`select`/tabla, límite de payload, rate limiter, aislamiento de rol/claims en pooling, endpoints administrativos sin token y rotación de pepper/AES sin interrumpir proyectos existentes. Desde P1.1: rutas DDL sin token, con token erróneo y con solo `apikey` público, inyección en identificadores y expresiones de política, DDL sobre una tabla que solo existe en otro proyecto y denegación de filas tras eliminar la política. Desde P1.3: rutas `/migrations` sin token, con token erróneo y con solo `apikey` público, inyección en el nombre de la migración y en identificadores de operaciones, historia invisible para el inspector y el Runtime, y aislamiento del historial entre proyectos. Desde P1.2: rutas `/sql/query` sin token, con token erróneo y con solo `apikey` público, siete variantes de escritura/inyección que no alteran datos, tabla inexistente sin SQL en el error, timeout forzado, auditoría sin sentencia y aislamiento entre proyectos. Desde P1.5: rutas `/extensions` sin token, con token erróneo y con solo `apikey` público, nombres fuera del allowlist (incluido SQL inyectado) rechazados antes de abrir conexión, ausencia de `dblink`/`plpython3u`, instalación aislada entre proyectos, verificación de `pg_extension.extowner`, idempotencia de install/uninstall y auditoría sin DDL ni roles físicos. Desde P1.4: cupos independientes entre dos proyectos reales, cuota compartida por dos claves del mismo proyecto, fallback por IP para claves inválidas, rango inválido rechazado, ruta `/rate-limit` sin token y con solo `apikey` público, y auditoría del cambio. Desde P1.6: las cinco rutas `/backups` sin token y con solo `apikey` público, ausencia de administración bajo `/rest/v1`, respuestas sin token/pepper/clave AES/`PGPASSWORD`, rutas relativas y absolutas fuera del patrón GUID rechazadas, hash corrupto que impide cualquier trabajo en PostgreSQL, y aislamiento de copias entre proyectos. Desde P1.7: rutas de auditoría sin token, con token erróneo y con solo `apikey` público, parámetros desconocidos y tokens fuera del charset, fechas invertidas, `limit`/`offset` fuera de rango, redacción de claves y JWT en `details`/`Error`, y lectura aislada entre proyectos sin generar auditoría nueva. Desde P2.1: ruta de vista previa sin token, con token erróneo y con solo `apikey` público, ejecución real como `anon` respetando política SELECT y ausencia de ella, inyección en tabla y columna rechazada por la allowlist, y aislamiento de filas entre dos proyectos reales.

## Notas de entorno (inspección 2026-11-16 / Fase 0)
- Desarrollo en Windows; homelab Ubuntu 26.04.1 LTS como entorno objetivo. Inventario del homelab pendiente.
- Solo PostgreSQL se ejecuta en Docker durante el desarrollo; PostgreSQL escucha en `127.0.0.1:5433` (el puerto `5432` local está ocupado por otro stack y no se toca).
- `.env.example` versionado con placeholders; secretos reales fuera de Git. La rotación del pepper y del cifrado AES está implementada y probada (`p0-4-secrets.md`); sigue pendiente decidir el gestor de secretos y la ubicación del backup antes del despliegue en el homelab.
