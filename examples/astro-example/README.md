# DomiBase · ejemplo Astro

Ejemplo SSR real que consume la API Runtime de DomiBase con el SDK propio
[`@domibase/client`](../../packages/client). Inserta, lista, actualiza y elimina contactos en
PostgreSQL mediante la clave pública del proyecto, e invoca una función WebAssembly aislada con
`db.functions.invoke`.

- Astro 7 con `@astrojs/node` en modo `standalone` y `output: "server"`.
- Sin Supabase, GoTrue ni PostgREST.
- Sin credenciales administrativas en el navegador ni en el bundle cliente.
- Variables de entorno declaradas con `astro:env/server` (`access: "secret"`).

## Requisitos

1. PostgreSQL de desarrollo en Docker (`domibase-postgres`, `127.0.0.1:5433`).
2. API DomiBase ejecutándose en `http://localhost:5080`.
3. SDK compilado en `packages/client/dist`.

## Puesta en marcha

Desde la raíz del repositorio:

```powershell
# 1. API en una terminal
dotnet run --project backend/src/DomiBase.Api

# 2. Preparar proyectos, tablas/RLS, claves y .env del ejemplo
node scripts/dev/phase-06-demo.mjs
```

El script:

- crea o reutiliza `paneles-solares` y `aislamiento-demo`, y espera su estado `Ready`;
- crea una clave pública por proyecto y revoca una clave adicional para la prueba de revocación;
- crea `public.contactos` con RLS y políticas de demo en la base de cada proyecto;
- siembra una fila distinta por proyecto;
- registra la función `demo-echo` (módulo WASM que devuelve el payload y el contexto del proyecto);
- escribe `examples/astro-example/.env` sin imprimir secretos;
- verifica CRUD, aislamiento, clave inválida, clave revocada, clave ausente e invocación de la
  función con el SDK.

Después:

```powershell
cd examples/astro-example
cmd /c npm install
cmd /c npm run dev
```

Abrir <http://localhost:4325> para el CRUD, <http://localhost:4325/funciones> para invocar la
función WASM con la sesión autenticada y <http://localhost:4325/diagnostico> para las comprobaciones
de seguridad en vivo. El puerto 4325 se fija en `astro.config.mjs` porque el 4321 por defecto puede
estar ocupado por otra aplicación local.

## Scripts

| Script | Uso |
|---|---|
| `npm run dev` | Compila el SDK y arranca Astro en modo desarrollo. |
| `npm run build` | Compila el SDK y genera el servidor `standalone` en `dist/`. |
| `npm run check` | Compila el SDK y ejecuta `astro check`. |
| `npm run start` | Ejecuta el build con `.env` (`node --env-file-if-exists=.env`). |

`npm install` necesita que el SDK tenga `dist/`; los scripts de Astro lo compilan antes de
arrancar. En una copia limpia ejecuta primero:

```powershell
cd packages/client
cmd /c npm install
cmd /c npm run build
```

## Variables

`.env.example` documenta las variables. `scripts/dev/phase-06-demo.mjs` genera el `.env` real:

| Variable | Uso |
|---|---|
| `DOMIBASE_API_URL` | URL del Runtime (`http://localhost:5080`). |
| `DOMIBASE_PROJECT_KEY` | Clave `db_public_` del proyecto principal. |
| `DOMIBASE_PROJECT_KEY_B` | Clave del segundo proyecto para la prueba de aislamiento. |
| `DOMIBASE_REVOKED_PROJECT_KEY` | Clave ya revocada para demostrar el rechazo. |
| `DOMIBASE_PROJECT_NAME` / `_B` | Nombres mostrados en la interfaz. |

Nunca uses el prefijo `PUBLIC_` en estas variables: las claves deben permanecer en el servidor.

## Diseño y seguridad

- El formulario usa POST + redirección 303 (patrón PRG) y validación en servidor además de la
  validación nativa del navegador.
- `security.checkOrigin` de Astro está activo para rechazar envíos de formulario cruzados.
- Cabeceras `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, `Cache-Control:
  no-store` y CSP en producción.
- Tabla semántica con `caption`, encabezados `scope="col"` y consultas de contenedor para ocultar
  columnas secundarias en espacios estrechos.
- Los mensajes de error del Runtime se muestran sin SQL, secretos ni detalles internos.
- Las políticas RLS de `contactos` son deliberadamente permisivas para el rol anónimo **solo para
  esta demo**. En un proyecto real se restringen por fila y operación.

## Limitaciones

- Es un ejemplo de desarrollo; el registro y login solo sirven para demostrar RLS y el contexto de
  las funciones, no sustituyen un sistema de autenticación de usuarios finales.
- No hay panel de administración ni creación de tablas desde la interfaz.
- Las funciones son síncronas y corren en un worker one-shot; el guest no tiene red ni llamadas de
  vuelta al Runtime.
- El rate limit del Runtime se mide por proyecto (P1.4) y puede afectar a pruebas muy intensivas; se ajusta solo desde la Control Plane con `PATCH /api/v1/projects/{id}/rate-limit`. Las claves inválidas o ausentes se limitan por IP con los defaults globales.
