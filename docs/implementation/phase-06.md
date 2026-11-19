# Fase 6 — Ejemplo Astro e integración real — completada

## Objetivo
Entregar `examples/astro-example`: una aplicación Astro SSR real que consume el SDK propio `@domibase/client` contra el Runtime API local, demostrando INSERT/SELECT/UPDATE/DELETE, aislamiento entre proyectos y rechazo de claves inválidas, revocadas y ausentes. Todo ello con **solo PostgreSQL en Docker**; Astro, API y SDK se ejecutan directamente en Windows.

## Entregado

### Aplicación Astro (`examples/astro-example`)
- Astro **7.4.12** con `@astrojs/node` **11.1.13**, `output: "server"` y adaptador `standalone`.
- Puerto fijo **4325** (`server.port` en `astro.config.mjs`): el `4321` por defecto estaba ocupado por otra aplicación local (proyecto Nexcard), y Astro intentaba saltar a otro puerto sin avisar.
- Entorno validado con `astro:env/server` (`access: "secret"`): las claves se leen solo en el servidor y nunca se declaran con prefijo `PUBLIC_`.
- Dependencia local `"@domibase/client": "file:../../packages/client"`; los scripts de Astro compilan el SDK antes de arrancar.
- Sin Supabase, GoTrue ni PostgREST.

### Páginas
- `src/pages/index.astro` — listado SSR de `contactos` y formulario de alta. POST + redirección **303** (patrón PRG) con validación en servidor además de la validación nativa del navegador. Acciones de actualizar estado y eliminar por fila.
- `src/pages/diagnostico.astro` — comprobaciones de seguridad ejecutadas **en el servidor**: lectura del proyecto principal, lectura del segundo proyecto, aislamiento bidireccional, bloqueo de la base de control (`domibase_control`), clave inválida, clave revocada y petición sin clave. Los resultados se renderizan con `data-check` / `data-status`.

### Cliente y seguridad
- `src/lib/domibase.ts` — factoría de clientes **solo servidor** a partir de `astro:env/server`; tipos de `Contacto`, columnas, estados y utilidades de error que nunca imprimen secretos.
- `src/middleware.ts` — cabeceras `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, `Cache-Control: no-store` y **CSP en producción**.
- `security.checkOrigin: true` en la config de Astro para rechazar envíos de formulario cruzados (defensa en profundidad; CORS no es autorización).
- `src/layouts/Layout.astro` — diseño responsivo y accesible: skip-link, navegación con `aria-current`, tabla semántica con `caption` y `scope="col"`, consultas de contenedor para ocultar columnas secundarias y avisos con `role="status"`/`role="alert"`.
- Sin `window` durante la importación del SDK; el único script inline es un confirm de borrado con `is:inline`.

### Esquema y datos de demo
`scripts/dev/phase-06-demo.mjs` prepara dos proyectos reales:

- `paneles-solares` (proyecto principal) y `aislamiento-demo` (segundo proyecto), ambos con estado `Ready`.
- Una clave pública `db_public_` por proyecto y una tercera clave revocada para la prueba de revocación.
- En cada base `domibase_<slug>` crea `public.contactos` (`id uuid`, `nombre`, `email UNIQUE`, `estado`, `notas`, `created_at`) con **RLS habilitado**, `GRANT` mínimo para el rol `_anon` y políticas de demo para SELECT/INSERT/UPDATE/DELETE.
- Siembra una fila distinta por proyecto.

> Las políticas de `contactos` son deliberadamente permisivas para el rol anónimo **solo para esta demo**. En un proyecto real se restringen por fila y operación.

El script **no imprime** valores de claves ni secretos; solo escribe `examples/astro-example/.env` (ignorado por Git).

### Verificador de formularios
`scripts/dev/phase-06-verify-astro.mjs` ejercita el ejemplo a través de HTTP real:

- `GET /` y cabeceras de seguridad.
- INSERT por formulario → 303 `?ok=created` → fila visible y presente en PostgreSQL.
- UPDATE por formulario → 303 `?ok=updated` → estado persistido y renderizado.
- DELETE por formulario → 303 `?ok=deleted` → fila retirada de PostgreSQL y del listado.
- `GET /diagnostico` → las 7 comprobaciones `pass` (sin `fail`).
- Comprueba que ni `/` ni `/diagnostico` incluyen `db_public_` en el HTML servido.
- Limpia la fila de prueba si algo falla.

## Verificación ejecutada

### Preparación (API real en `http://localhost:5080`)
```powershell
dotnet run --project backend/src/DomiBase.Api
node scripts/dev/phase-06-demo.mjs
```

Salida:
```
Fase 6 · preparación de la demo Astro
  [OK] API lista en http://localhost:5080
  [OK] Proyectos Ready: paneles-solares, aislamiento-demo
  [OK] Claves públicas creadas y clave de prueba revocada
  [OK] Tablas contactos con RLS, GRANT y filas de demo preparadas
  [OK] CRUD completo (INSERT → SELECT → UPDATE → DELETE) verificado
  [OK] Aislamiento bidireccional y bloqueo de domibase_control verificados
  [OK] Clave inválida, clave revocada y clave ausente rechazadas con 401
  [OK] CRUD, aislamiento y rechazo de claves verificados con @domibase/client
  [OK] examples/astro-example/.env escrito (sin imprimir valores)
```

### Dependencias, tipos y build
```powershell
cd examples/astro-example
cmd /c npm install        # 287 paquetes, 0 vulnerabilidades
cmd /c npm run check     # 0 errores, 0 warnings, 0 hints
cmd /c npm run build     # output: "server" (standalone) correcto
```

### Verificación en desarrollo (`cmd /c npm run dev`, puerto 4325)
```powershell
node scripts/dev/phase-06-verify-astro.mjs
```
Salida:
```
  [OK] Portada SSR y cabeceras de seguridad
  [OK] INSERT desde el formulario Astro visible en el listado
  [OK] UPDATE desde el formulario Astro persistido y renderizado
  [OK] DELETE desde el formulario Astro persistido y retirado del listado
  [OK] Diagnóstico en vivo: 7 comprobaciones en OK
```

### Verificación en producción (`PORT=4325 npm run start`)
Mismos 5 bloques en OK. Cabecera CSP confirmada:
```
default-src 'self'; base-uri 'self'; connect-src 'self'; form-action 'self';
frame-ancestors 'none'; img-src 'self' data:; object-src 'none';
script-src 'self'; style-src 'self' 'unsafe-inline'
```

### Suite backend
```powershell
dotnet test backend/DomiBase.sln
```
**98 tests en verde** (73 unitarios, 19 integración, 6 seguridad). Sin cambios en el backend durante la Fase 6; la suite se repitió para confirmar que no se rompió nada.

### Tests del SDK
```powershell
cd packages/client
cmd /c npm test
```
**50 tests en verde**. `insert` pasó a aceptar `Partial<TRow>` (o `readonly Partial<TRow>[]`) para permitir filas que dependen de valores por defecto de PostgreSQL; el resto del contrato no cambió.

## Seguridad demostrada
- La clave pública **nunca** llega al navegador: solo se usa en el servidor de Astro (`astro:env/server`) y en los scripts de desarrollo; el verificador confirma que el HTML de `/` y `/diagnostico` no contiene `db_public_`.
- El ejemplo es **SSR**: el navegador no llama directamente a `/rest/v1`, por lo que el CORS por defecto de Development (`http://localhost:4321`) no interfiere. Si en el futuro el SDK se usa en el navegador contra el ejemplo (origen `http://localhost:4325`), definir `DOMIBASE_CORS_ORIGINS` con ese origen.
- El proyecto se resuelve **server-side** por hash HMAC; el cliente no envía `projectId`, rol ni SQL.
- Aislamiento real entre bases `domibase_paneles_solares` y `domibase_aislamiento_demo`: ninguna fila cruzada es legible en ninguno de los dos sentidos.
- `domibase_control` responde `404 TABLE_NOT_FOUND` desde el Runtime.
- Clave inválida y clave revocada → `401 INVALID_API_KEY`; petición sin clave → `401 MISSING_API_KEY`.
- RLS y `GRANT` siguen decidiendo cada fila y operación; el Runtime no usa roles propietarios ni superuser.

## Limitaciones actuales
- Es un ejemplo de desarrollo; no implementa autenticación de usuarios finales ni panel de administración.
- Las políticas RLS de la demo son amplias a propósito; un proyecto real las restringe por fila/operación.
- No hay creación de tablas ni políticas desde la interfaz.
- En esta fase el rate limit del Runtime era por IP y podía afectar a pruebas muy intensivas; P1.4 lo sustituye por una cuota por proyecto (`docs/implementation/p1-4-rate-limits.md`).
- El puerto 4325 se eligió por estar libre; si cambia, ajustar `astro.config.mjs` y los scripts de verificación.

## Siguiente
- Fase 7 — validación final (persistencia tras reinicio, múltiples bases/roles/RLS/pooling y revisión de seguridad).
- Fase 8 — preparación del Dockerfile de la API (sin construir ni publicar).
