# Auth — subfase 2.3: endpoints `/auth/v1` — completada

Tercera subfase de la Fase 2 de producto (ver `auth-plan.md`). Expone el registro, login, refresh y logout de usuarios finales por proyecto.

## Entregado

### `AuthService`
- Ubicación: `DomiBase.Application/Auth/AuthService.cs`.
- Responsabilidades:
  - `RegisterAsync`: valida email/contraseña, hashea con Argon2id, crea usuario, emite sesión.
  - `LoginAsync`: busca usuario, verifica hash con comparación de tiempo constante, rechaza cuentas deshabilitadas.
  - `RefreshAsync`: valida refresh token, rota atómicamente, detecta reuse y revoca toda la familia.
  - `LogoutAsync`: revoca el refresh token por hash (idempotente).
- Emite access tokens ES256 con la clave activa del proyecto (`IAuthSigningKeyService`).
- Nunca devuelve detalles internos de error (mismo mensaje genérico para credenciales inválidas, token expirado o usuario deshabilitado).

### Endpoints
Nuevo grupo bajo `/auth/v1`, resolviendo el proyecto por la cabecera `apikey`:

- `POST /auth/v1/register` — `{ email, password }` → `{ data: { accessToken, refreshToken, expiresAt, user } }`.
- `POST /auth/v1/login` — `{ email, password }` → mismo contrato.
- `POST /auth/v1/refresh` — `{ refreshToken }` → nueva sesión.
- `POST /auth/v1/logout` — `{ refreshToken }` → `{ data: null }`.

Todos los endpoints usan el envelope canónico `{ data }` / `{ error }` para consistencia con el SDK.

### Configuración
- `AuthOptions` con `AccessTokenLifetime` (15 min por defecto) y `RefreshTokenLifetime` (7 días por defecto).
- Sobreescribible mediante `AUTH_ACCESS_TOKEN_LIFETIME_MINUTES` y `AUTH_REFRESH_TOKEN_LIFETIME_DAYS`.

### Cambios en `JwtAccessTokenService`
- Firma/validación con instancia fresca de `JwtSecurityTokenHandler` por operación (no es thread-safe).
- `CryptoProviderFactory` con `CacheSignatureProviders = false` para evitar el problema de `ObjectDisposedException` al reutilizar ECDsa en Windows.

## Verificación ejecutada

```powershell
dotnet test backend/DomiBase.sln
```

- **138 tests en verde** (97 unitarios + 35 integración + 6 seguridad).
- Tests nuevos:
  - `AuthEndpointsTests`: registro, login, email duplicado, credenciales inválidas, rotación de refresh token, reuse revoca familia, logout revoca token, apikey ausente.
  - `AuthServiceDirectTests`: flujo directo de registro + login contra servicios reales.

## Siguiente
Subfase **2.4 — Runtime autenticado**: validar Bearer en `/rest/v1`, aplicar rol `authenticated` y claims `sub` en RLS.
