# DomiBase · ejemplo Astro

Ejemplo SSR real que consume la API Runtime de DomiBase con `@domibase/client@0.1.0` instalado
desde npm. Inserta, lista, actualiza y elimina contactos en PostgreSQL mediante la clave pública
del proyecto. La página `/diagnostico` comprueba en vivo el aislamiento entre proyectos y el rechazo
de claves inválidas, revocadas y ausentes.

> **Alcance Fase 1:** `createClient`, `from` y el query builder. El paquete publicado no incluye
> `auth`, `realtime`, `storage` ni `functions`; las páginas y helpers que los usaban se retiraron de
> este ejemplo. El código de esas capacidades sigue conservado como diferido en
> `packages/client/deferred` para fases posteriores.

- Astro 7 con `@astrojs/node` en modo `standalone` y `output: "server"`.
- Sin Supabase, GoTrue ni PostgREST.
- Sin credenciales administrativas en el navegador ni en el bundle cliente.
- Variables de entorno declaradas con `astro:env/server` (`access: "secret"`).

## Requisitos

1. Stack Docker local (`docker compose up -d --build` desde la raíz: `domibase-postgres`, `domibase-api` y `domibase-admin`) o, en modo clásico, solo PostgreSQL en Docker (`domibase-postgres`, `127.0.0.1:5433`).
2. API DomiBase accesible en `http://localhost:5080`.
3. SDK local compilado solo para el script de preparación
   (`npm --prefix packages/client run build`). La aplicación Astro usa el paquete de npm.

## Puesta en marcha

Desde la raíz del repositorio:

```powershell
# 1. API en una terminal
dotnet run --project backend/src/DomiBase.Api

# 2. Preparar proyectos, tabla/RLS, claves y .env del ejemplo
node scripts/dev/phase-06-demo.mjs
```

El script:

- crea o reutiliza `paneles-solares` y `aislamiento-demo`, y espera su estado `Ready`;
- crea una clave pública por proyecto y revoca una clave adicional para la prueba de revocación;
- crea `public.contactos` con RLS y políticas de demo en la base de cada proyecto;
- siembra una fila distinta por proyecto;
- escribe `examples/astro-example/.env` sin imprimir secretos;
- verifica CRUD, aislamiento y rechazo de claves con el SDK.

Después:

```powershell
cd examples/astro-example
cmd /c npm install
cmd /c npm run dev
```

Abrir <http://localhost:4325> para el CRUD y <http://localhost:4325/diagnostico> para las
comprobaciones de seguridad en vivo. El puerto 4325 se fija en `astro.config.mjs` porque el 4321
por defecto puede estar ocupado por otra aplicación local.

## Scripts

| Script | Uso |
|---|---|
| `npm run dev` | Arranca Astro en modo desarrollo. |
| `npm run build` | Genera el servidor `standalone` en `dist/`. |
| `npm run check` | Ejecuta `astro check`. |
| `npm run start` | Ejecuta el build con `.env` (`node --env-file-if-exists=.env`). |

La dependencia `@domibase/client` se instala desde el registry; estos scripts no compilan el SDK
local.

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

- El ejemplo cubre únicamente la API Fase 1 del SDK publicado; no hay login, notas, almacenamiento,
  funciones ni tiempo real.
- No hay panel de administración ni creación de tablas desde la interfaz.
- El rate limit del Runtime se mide por proyecto (P1.4) y puede afectar a pruebas muy intensivas; se
  ajusta solo desde la Control Plane con `PATCH /api/v1/projects/{id}/rate-limit`. Las claves
  inválidas o ausentes se limitan por IP con los defaults globales.
