# Auth — subfase 2.1: fundamentos criptográficos — completada

Primera subfase de la Fase 2 de producto (ver `auth-plan.md` para las decisiones de diseño). No toca base de datos ni endpoints: entrega las primitivas criptográficas con tests unitarios.

## Entregado

### Contratos (`DomiBase.Application/Auth`)
- `IPasswordHasher` — `Hash(password)` y `Verify(passwordHash, password)`.
- `AccessTokens.cs` — `AccessTokenClaims` (`sub`, `pid`, `akid`, `role`), `IssuedAccessToken`, `AccessTokenValidationResult` y `IAccessTokenService` (emisión con ECDSA privada y validación con pública, sin exponer detalles internos).
- `RefreshToken` — `Generate()` (32 bytes aleatorios, base64url de 43 caracteres) y `Hash(token)` (SHA-256 en hexadecimal minúscula; la entropía de 256 bits no requiere pepper).

### Implementaciones (`DomiBase.Infrastructure/Auth`)
- `Argon2PasswordHasher` — Argon2id con `m=65536`, `t=3`, `p=1`, salt de 16 bytes y hash de 32 bytes; serialización **PHC** (`$argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>`), comparación con `CryptographicOperations.FixedTimeEquals` y rechazo seguro de hashes malformados.
- `AuthKeyFactory` — genera pares **ECDSA P-256**, exporta/importa SPKI (pública) y PKCS#8 (privada), y calcula un `kid` estable de 16 caracteres derivado del SHA-256 de la clave pública.
- `JwtAccessTokenService` — emisión/validación **ES256** con `iss=aud=domibase`, claims `sub`/`pid`/`akid`/`role`/`jti`, algoritmo fijado a ES256, `MapInboundClaims = false` para conservar los nombres compactos, y error genérico `invalid_token` ante firma inválida, expiración, audiencia/emisor incorrectos o formato inválido.

### Dependencias añadidas (`DomiBase.Infrastructure.csproj`)
- `Konscious.Security.Cryptography.Argon2` **1.3.1**.
- `System.IdentityModel.Tokens.Jwt` **8.23.0**.

## Verificación ejecutada
```powershell
dotnet test backend/DomiBase.sln
```
- **120 tests en verde**: 95 unitarios (73 previos + 22 nuevos), 19 integración y 6 seguridad.
- Nuevos tests: `Argon2PasswordHasherTests` (7), `AuthKeyFactoryTests` (3), `JwtAccessTokenServiceTests` (9) y `RefreshTokenTests` (3).
- Incidencia resuelta durante la verificación: `JwtSecurityTokenHandler` mapeaba `sub` a `http://schemas.xmlsoap.org/.../nameidentifier` al validar; se desactivó con `MapInboundClaims = false`.

## Notas de seguridad
- Ninguna contraseña, clave privada ni refresh token se registra en logs.
- La clave privada se exporta solo como PKCS#8 para ser cifrada con `DOMIBASE_SECRETS_KEY` (subfase 2.2), nunca en claro en la base de control.
- La validación JWT no acepta otros algoritmos (incluido `none`) ni claves distintas de la del proyecto.

## Siguiente
Subfase **2.2 — esquema y provisioning**: rol `<db>_authenticated`, esquema `auth` con `users`/`refresh_tokens`, almacenamiento de claves de firma en `domibase_control` y reprovision de los proyectos demo.
