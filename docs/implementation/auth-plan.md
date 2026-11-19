# Fase 2 de producto — Auth — plan y decisiones

## Objetivo
Usuarios finales **por proyecto** con registro, login, logout y refresh, contraseñas con **Argon2id**, **JWT asimétrico**, **refresh tokens rotatorios y revocables**, y contexto de usuario autenticado aplicado a RLS (`authenticated`). Nada de Auth como mock: todo contra PostgreSQL real.

## Decisiones de diseño

### Almacenamiento por proyecto
- Los usuarios y los refresh tokens viven en la **base de cada proyecto** (`domibase_<slug>`), en un esquema **`auth`** separado de `public`.
- El Runtime solo expone tablas de `public`, por lo que `auth.users` y `auth.refresh_tokens` **nunca** son accesibles vía `/rest/v1`.
- Las tablas se crean desde el **provisioning** con el rol `_owner`, igual que el resto del esquema del proyecto. No hay auto-migraciones al arrancar la API.

### Roles PostgreSQL
- Nuevo rol funcional `<db>_authenticated` (`NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`).
- El login `_runtime` recibe `GRANT <db>_authenticated` además del `_anon` actual, para poder hacer `SET ROLE` dentro de la transacción.
- Sin Bearer → `_anon` (comportamiento actual). Con Bearer válido → `authenticated` y claims con `sub`.

### Contraseñas (Argon2id)
- Paquete `Konscious.Security.Cryptography.Argon2`.
- Parámetros: `m=65536` (64 MiB), `t=3`, `p=1`, salt de 16 bytes, hash de 32 bytes.
- Almacenamiento en formato **PHC**: `$argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>`.
- Verificación con comparación de tiempo constante (`CryptographicOperations.FixedTimeEquals`).

### JWT de acceso (asimétrico)
- **ES256 (ECDSA P-256)** con clave **por proyecto**. La privada se cifra con `DOMIBASE_SECRETS_KEY` (AES-256-GCM, mismo mecanismo que la contraseña de Runtime) en `domibase_control`; la pública se guarda en claro.
- Claims: `sub` (id de usuario), `pid` (id de proyecto), `role=authenticated`, `jti`, `iat`, `exp`, `iss=domibase`, `aud=domibase`.
- Vida corta (15 minutos por defecto). `kid` = huella de la clave pública, para permitir rotación.
- Paquete `System.IdentityModel.Tokens.Jwt`.

### Refresh tokens
- 32 bytes aleatorios en base64url. Se guarda **SHA-256(token)** en hexadecimal (entropía de 256 bits: no necesita pepper, y rotar el pepper no invalida sesiones).
- Tabla `auth.refresh_tokens` con `family_id`, `expires_at`, `consumed_at`, `revoked_at`.
- **Rotación:** cada refresh marca el token como consumido y emite uno nuevo de la misma familia.
- **Reuso detectado:** presentar un token ya consumido revoca **toda la familia** del usuario.

### Endpoints
Nuevo grupo **`/auth/v1`** (ni Control `/api/v1`, ni datos `/rest/v1`), resolviendo el proyecto por la cabecera `apikey` pública, igual que el Runtime:
- `POST /auth/v1/register`
- `POST /auth/v1/login`
- `POST /auth/v1/refresh`
- `POST /auth/v1/logout`

### RLS autenticado
- Claims por transacción: además de `domibase.project_id` y `domibase.api_key_id`, se añade `request.jwt.claims` con `sub` y `role=authenticated`, y `domibase.user_id`.
- Las políticas de las tablas del proyecto deciden por fila con `current_setting('domibase.user_id', true)`.

## Subfases
- **2.1 Fundamentos criptográficos** — **completada** (ver `auth-01-foundations.md`): `Argon2PasswordHasher`, claves ECDSA, emisión/validación JWT, generador/hash de refresh tokens, con tests unitarios. Sin cambios de base de datos.
- **2.2 Esquema y provisioning:** rol `authenticated`, esquema `auth`, tablas, claves de firma en control y reprovision de los proyectos demo.
- **2.3 Endpoints Auth:** register/login/refresh/logout con tests de integración (incluidos rotación y reuso).
- **2.4 Runtime autenticado:** Bearer → `SET ROLE authenticated` + claims `sub`, políticas RLS y security tests.
- **2.5 SDK y ejemplo:** métodos `auth.*` en `@domibase/client` y flujo en el ejemplo Astro.

## Restricciones mantenidas
- Sin GoTrue, sin Supabase, sin PostgREST.
- Solo PostgreSQL en Docker; API y tests en Windows.
- Sin commits/pushes; sin despliegue; sin construir la imagen.
