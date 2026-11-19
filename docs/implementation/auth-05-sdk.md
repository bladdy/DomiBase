# Auth — subfase 2.5: SDK y ejemplo Astro — completada

Quinta y última subfase de la Fase 2 de producto (ver `auth-plan.md`).

## Entregado

### SDK `@domibase/client`
Nuevo módulo `auth.ts`:

- `AuthSession` — `accessToken`, `refreshToken`, `expiresAt`, `user`.
- `AuthUser` — `id`, `email`, `createdAt`.
- `AuthClient`:
  - `signUp({ email, password })`
  - `signInWithPassword({ email, password })`
  - `signOut()`
  - `refreshSession(refreshToken)`
  - `getSession()` / `setSession(session)`
  - `onAuthStateChange(handler)`

El `HttpTransport` incluye automáticamente `Authorization: Bearer <accessToken>` en peticiones al Runtime cuando hay una sesión activa.

### Cambios en el contrato de respuesta
Los endpoints `/auth/v1` ahora devuelven el envelope canónico `{ data: { ... } }` / `{ error }`, igual que el Runtime, para que el SDK pueda reutilizar `HttpTransport.send`.

### Ejemplo Astro
- Nueva página `/login` con formulario de registro/login.
- Nueva página `/notas` con CRUD autenticado sobre `public.notas`.
- Sesión persistida en cookies `httpOnly`, `Secure` en producción, `SameSite=Lax`.
- Helpers en `src/lib/domibase.ts`: `readSessionFromCookies`, `serializeSession`, `authenticatedClient`, `listNotes`, `createNote`.
- Actualizado `Layout.astro` para enlazar a Notas.

### Script de demo
`scripts/dev/phase-06-demo.mjs` ahora crea también la tabla `public.notas` con políticas RLS para `anon` y `authenticated`.

## Verificación ejecutada

SDK:

```powershell
cd packages/client
cmd /c npm run build
cmd /c npm test
```

- **57 tests en verde** (7 nuevos de auth).

Backend:

```powershell
dotnet test backend/DomiBase.sln
```

- **138 tests en verde**.

## Fase 2 completa

Con esta subfase termina la Fase 2 de producto (Auth). El roadmap se actualiza para marcar todos los puntos de la fase como completados.
