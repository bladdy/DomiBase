# P0.3 — API Keys: secretas, rotación y expiración

**Estado:** completado y verificado (2026-11-17).

## 1. Funcionalidades implementadas

- `POST /api/v1/projects/{id}/keys` acepta `kind: "secret"` y emite claves `db_secret_` con la misma entropía y hash HMAC-SHA-256 con pepper que las públicas.
- Las claves secretas **pueden emitirse, listarse, revocarse y rotarse**, pero el Runtime las rechaza siempre en el header `apikey`: no elevan privilegios ni seleccionan un rol PostgreSQL distinto.
- `POST /api/v1/projects/{id}/keys/{keyId}/rotate`:
  - genera una clave nueva del mismo tipo, con un valor completo que se muestra **una sola vez**;
  - la clave anterior queda revocada en su fila, pero su valor sigue resolviendo durante una ventana de solapamiento;
  - el solapamiento por defecto es 60 minutos, configurable de 0 a 1440 minutos mediante `{ "overlapMinutes": n }`. Con 0 la clave antigua deja de funcionar de inmediato;
  - la expiración original se conserva en la nueva clave.
- `expiresAt` opcional en la creación: fecha futura, aplicada y verificada por el servidor en cada petición del Runtime.
- Listado y respuesta de creación/rotación nunca exponen hashes, ni el hash anterior de una rotación.
- Auditoría: `api_key.rotate` sobre la clave nueva y `api_key.revoke` con `Reason: rotate` sobre la anterior.

## 2. Arquitectura modificada

- `ApiKey` gana `ExpiresAt`, `PreviousSecretHash` y `PreviousHashExpiresAt`, más `IsExpired(now)`.
- `RuntimeProjectResolver` realiza dos búsquedas acotadas:
  1. clave actual activa, no revocada, no expirada y de tipo `Public`;
  2. si no hay coincidencia, hash anterior dentro de la ventana de solapamiento, también solo `Public`.
  La fila antigua está revocada, por lo que nunca puede resolverse por su propio hash.
- `ProjectService` centraliza validación de tipo, expiración y solapamiento, y añade la fila nueva **antes** de revocar la anterior para que un fallo de unicidad no desactive una clave en uso.
- `ControlEndpoints` añade la ruta de rotación; todas las rutas `/api/v1` siguen detrás del middleware de token administrativo.

## 3. Archivos creados

- `backend/src/DomiBase.Infrastructure/Persistence/Migrations/20261117225753_ApiKeyRotationAndExpiry.cs` (y su `.Designer.cs`)
- `backend/tests/DomiBase.IntegrationTests/ApiKeyLifecycleTests.cs`
- `docs/implementation/p0-3-api-keys.md`

## 4. Archivos modificados

- `backend/src/DomiBase.Domain/Projects/ApiKey.cs`
- `backend/src/DomiBase.Domain/Projects/ApiKeyKind.cs`
- `backend/src/DomiBase.Infrastructure/Persistence/Configurations/ApiKeyConfiguration.cs`
- `backend/src/DomiBase.Application/Projects/ProjectService.cs`
- `backend/src/DomiBase.Infrastructure/Runtime/RuntimeProjectResolver.cs`
- `backend/src/DomiBase.Api/Contracts/ProjectContracts.cs`
- `backend/src/DomiBase.Api/Endpoints/ControlEndpoints.cs`
- `backend/tests/DomiBase.IntegrationTests/ControlPlaneProjectTests.cs` (la emisión secreta dejó de esperar `400`)
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs`
- `docs/implementation/roadmap.md`
- `README.md`
- `docs/architecture/overview.md`

## 5. Migraciones creadas y aplicadas

`20261117225753_ApiKeyRotationAndExpiry`: añade `expires_at`, `previous_secret_hash`, `previous_hash_expires_at` e índice no único sobre `previous_secret_hash` en `api_keys`. Aplicada con `dotnet ef database update`. La migración es aditiva y no bloquea el arranque de versiones anteriores.

## 6. Pruebas ejecutadas

- `dotnet build backend/DomiBase.sln`: 0 errores, 0 warnings.
- `dotnet ef database update`: migración aplicada correctamente.
- `dotnet test backend/DomiBase.sln`: **203/203** en verde.
  - UnitTests: 116.
  - IntegrationTests: 76 (5 nuevos de ciclo de vida de claves).
  - SecurityTests: 11 (1 nuevo: rotación sin token → `401`).

Casos nuevos relevantes: clave secreta emitida y rechazada por Runtime (incluso tras rotarla); solapamiento con ambas claves válidas y rechazo de la antigua al cerrarse la ventana; rotación con solapamiento cero; expiración forzada en base; validación de solapamiento, clave inexistente, clave revocada y fecha de expiración pasada; ausencia de hashes en listados.

## 7. Resultados

Se cumplen los criterios P0.3: emisión y gestión de claves secretas sin elevación, rotación con convivencia temporal, expiración server-side y auditoría, sin cambiar los contratos existentes del SDK ni el comportamiento de las claves públicas ya emitidas.

## 8. Riesgos identificados

- La rotación no usa bloqueo optimista: dos rotaciones concurrentes de la misma clave podrían crear dos reemplazos. La ventana es pequeña y ambas dejan la clave original revocada, pero conviene añadir una restricción de concurrencia en una iteración futura.
- Solo se conserva **una** generación anterior por fila. Rotar el reemplazo antes de cerrar el solapamiento acorta la validez del valor más antiguo; está documentado y es deliberado para no crecer indefinidamente.
- El solapamiento se evalúa con la hora del servidor (`DateTimeOffset.UtcNow`). Un reloj desincronizado en el host alteraría la ventana.
- La expiración se comprueba en el resolver; una petición ya autorizada y en curso no se cancela al expirar la clave.

## 9. Limitaciones pendientes

- No hay keyring ni rotación del pepper: los hashes se recalculan con el pepper actual. Eso es P0.4.
- No hay UI en el panel para emitir/rotar claves; la gestión es por API de Control Plane.
- Las claves secretas no autorizan ningún endpoint hoy: son material para futuras operaciones server-side.

## 10. Siguiente fase

**P0.4 — Secretos**: keyring multi-clave, rotación del pepper de API keys y del cifrado de credenciales de runtime, con procedimientos de respaldo y verificación de que los valores antiguos siguen siendo verificables/descifrables.
