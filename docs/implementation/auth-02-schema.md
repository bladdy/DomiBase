# Auth — subfase 2.2: esquema y provisioning — completada

Segunda subfase de la Fase 2 de producto (ver `auth-plan.md`). Extiende el provisioning y la capa de infraestructura para soportar usuarios finales por proyecto.

## Entregado

### Roles PostgreSQL por proyecto
- Nuevo rol funcional `<db>_authenticated` (`NOLOGIN`, sin privilegios de administración, sin `BYPASSRLS`).
- El login `<db>_runtime` recibe `GRANT <db>_authenticated`, por lo que puede hacer `SET ROLE` dentro de una transacción a los roles funcionales `anon` y `authenticated`.
- `NOINHERIT` en el runtime login evita que los privilegios se activen accidentalmente fuera de una transacción con `SET ROLE`.

### Esquema `auth` por proyecto
Creado por `ProjectProvisioner.ApplyAuthSchemaAsync` en cada base `domibase_<slug>`:

- `auth.users`
  - `id uuid PRIMARY KEY DEFAULT gen_random_uuid()`
  - `email text NOT NULL`
  - `password_hash text NOT NULL`
  - `created_at timestamptz NOT NULL DEFAULT now()`
  - `disabled_at timestamptz`
  - Índice único `lower(email)`.

- `auth.refresh_tokens`
  - `id uuid PRIMARY KEY DEFAULT gen_random_uuid()`
  - `user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE`
  - `family_id uuid NOT NULL`
  - `token_hash text NOT NULL UNIQUE`
  - `created_at timestamptz NOT NULL DEFAULT now()`
  - `expires_at timestamptz NOT NULL`
  - `consumed_at timestamptz`
  - `revoked_at timestamptz`
  - Índices por `user_id` y `family_id`.

El esquema `auth` **nunca** se expone por la API pública de datos (`/rest/v1`): el Runtime solo sirve tablas de `public`.

### Claves de firma en `domibase_control`
- Entidad `ProjectAuthKey` con `kid`, clave pública SPKI en claro y clave privada PKCS#8 cifrada con AES-256-GCM (`DOMIBASE_SECRETS_KEY`).
- `EfAuthSigningKeyService` crea o reutiliza una clave activa por proyecto de forma idempotente (índice parcial único).
- Migración `AuthSigningKeys` ya aplicada.

### Acceso SQL a `auth`
- `NpgsqlAuthUserRepository` implementa `IAuthUserRepository`.
- Usa `RuntimeConnectionFactory` para conectar con el login runtime del proyecto.
- Todos los valores están parametrizados; identificadores allow-listeados.
- Rotación de refresh tokens atómica (`FOR UPDATE` + insert), con detección de reuse (devuelve `false` si el token ya fue consumido o revocado).

### Limpieza de tests
`ProjectCleanup` ahora también elimina el rol `<db>_authenticated` al borrar un proyecto de prueba.

## Verificación ejecutada

```powershell
dotnet test backend/DomiBase.sln
```

- **125 tests en verde**: 95 unitarios, 24 integración (19 previos + 5 nuevos de auth repository), 6 seguridad.
- Los nuevos tests de integración validan:
  - Creación y búsqueda de usuarios por email e id.
  - Rechazo de email duplicado.
  - Ciclo completo de refresh token: crear, buscar, rotar, revocar por familia y por hash.
  - Detección de rotación sobre token ya consumido.

## Siguiente
Subfase **2.3 — endpoints Auth**: `AuthService` + endpoints `/auth/v1/register|login|refresh|logout` con tests de integración de rotación, reuso y revocación.
