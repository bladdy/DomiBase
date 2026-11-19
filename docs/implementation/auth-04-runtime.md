# Auth — subfase 2.4: Runtime autenticado — completada

Cuarta subfase de la Fase 2 de producto (ver `auth-plan.md`). El Runtime ahora puede ejecutar peticiones con el rol funcional `authenticated` y los claims del JWT.

## Entregado

### `RuntimeProjectContext`
- Nuevo campo `AuthenticatedRole` poblado por `RuntimeProjectResolver`.
- El contexto sigue sin exponer credenciales; solo contiene nombres de roles funcionales.

### Validación de Bearer
- `RuntimeEndpoints` lee la cabecera `Authorization: Bearer <token>`.
- Resuelve la clave pública activa del proyecto (`IAuthSigningKeyService`) y valida el token con `IAccessTokenService`.
- Comprueba que `pid`, `akid` y `role=authenticated` coincidan con el proyecto de la API key.
- Si el Bearer está presente pero es inválido → `401 INVALID_TOKEN`.
- Si no hay Bearer → comportamiento anónimo actual.

### Contexto de transacción
`RuntimeDataService.ApplyRequestContextAsync` ahora:
- Usa `context.AuthenticatedRole` cuando el token es válido.
- Incluye `sub` en `request.jwt.claims`.
- Establece `domibase.user_id` para políticas RLS.
- Sin token: mantiene `role=anon`, sin `sub` ni `domibase.user_id`.

La configuración es transaccional (`SET ... true`) y desaparece al cerrar la transacción; el pool de conexiones no filtra identidad entre peticiones.

### Tests
Nueva colección `RuntimeAuthCollection` con fixture que crea una tabla `public.notas` protegida por RLS:
- Anónimo: `USING (false)` / `WITH CHECK (false)`.
- Autenticado: filas donde `owner_id = current_setting('domibase.user_id', true)::uuid`.
- La columna `owner_id` tiene `DEFAULT current_setting('domibase.user_id', true)::uuid`.

Tests (`RuntimeAuthTests`):
- Usuario autenticado inserta y lee sus propias filas.
- Usuario anónimo no ve filas autenticadas.
- Bearer inválido devuelve 401.
- Token manipulado es rechazado.

## Verificación ejecutada

```powershell
dotnet test backend/DomiBase.sln
```

- **138 tests en verde**.

## Siguiente
Subfase **2.5 — SDK y ejemplo Astro**: métodos `auth.*` en `@domibase/client` y flujo de login en Astro.
