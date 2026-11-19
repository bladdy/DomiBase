# DomiBase

Herramienta backend privada, administrada por un único propietario, con APIs y SDK TypeScript propios. Construida con ASP.NET Core 10 y PostgreSQL 18.

## Flujo operativo

`Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK`

El panel (`frontend/admin`) tiene intencionalmente una superficie mínima:

- **Proyectos:** crear, abrir, editar el cupo de Runtime y eliminar con confirmación por slug.
- **Dentro de un proyecto:** **Resumen**, **Table Editor** y **Conexión**, más **Volver a proyectos**.
- No hay pestañas de Auth de usuarios finales, Storage, Realtime, Functions, SQL Editor, Migraciones, Extensiones, Backups ni Auditoría. Su código backend/shared se conserva, pero no forma parte de la interfaz ni del SDK soportado.

Detalle de decisiones, endpoints y verificación: `docs/implementation/admin-minimal-scope.md`.

## Tecnologías

- ASP.NET Core 10 / C# — un único host con áreas lógicas **Control Plane** y **Runtime Plane**.
- PostgreSQL 18 (imagen fija `postgres:18-alpine`), un servidor compartido y una base independiente por proyecto.
- Npgsql + EF Core para metadatos de control; Npgsql directo para el Runtime.
- SDK TypeScript propio: `@domibase/client` (paquete provisional, `private`).
- React + Vite + TypeScript en `frontend/admin`.
- Astro 7 SSR en `examples/astro-example`.

## Arquitectura

- **Control Plane:** login del propietario, proyectos, provisioning, API keys, esquema/RLS tipados y metadatos. Endpoints privados bajo `/api/v1` y `/admin/v1`. Nunca accesible con una clave pública.
- **Runtime Plane:** API pública de datos `/rest/v1`, resuelta server-side por API key, con GRANT/RLS reales, SQL parametrizado y errores normalizados. No expone administración.
- Ambas áreas comparten proceso pero separan servicios, autenticación y conexiones; la extracción futura no requiere rediseñar contratos.
- Solo PostgreSQL corre en Docker durante el desarrollo (`127.0.0.1:5433`). API, panel, SDK, ejemplo y tests corren directamente en Windows.

## Arranque local

```powershell
docker compose up -d                              # solo PostgreSQL
dotnet run --project backend/src/DomiBase.Api     # API en http://127.0.0.1:5080
cd frontend/admin; cmd /c npm run dev             # panel en http://127.0.0.1:5190
```

El panel usa usuario/contraseña de `DOMIBASE_ADMIN_USERNAME` / `DOMIBASE_ADMIN_PASSWORD` y guarda la sesión `dbsess_` firmada solo en `sessionStorage`. `DOMIBASE_ADMIN_TOKEN` queda reservado a servicios y tests. Copia `.env.example` a `.env` y genera valores reales fuera de Git.

## Estado verificado

- Backend: **491 tests** en verde (311 unitarios, 134 de integración contra PostgreSQL real, 46 de seguridad).
- SDK: **70 tests** en verde; exports activos limitados a `createClient`, tipos, `QueryBuilder` y `DomiBaseError`.
- Panel: **57 tests** en verde y `tsc --noEmit` limpio.
- Ejemplo Astro: `astro check` sin errores.
- Criterio E2E: crear `paneles-solares`, provisionar su base, crear `contactos`, insertar y leer un contacto con el SDK, y demostrar aislamiento/RLS. Registro en `docs/implementation/admin-minimal-e2e.md`.

## Principio

Implementación propia. Sin `@supabase/supabase-js`, GoTrue ni PostgREST. Sin billing, planes, organizaciones ni registro de clientes.

## Documentación

- `AGENTS.md` — decisiones y reglas para agentes de desarrollo.
- `PRODUCT.md` — superficie y principios del producto.
- `docs/architecture/` — arquitectura, contrato REST y seguridad.
- `docs/implementation/admin-minimal-scope.md` — estado vigente del panel minimalista.
- `docs/implementation/` — roadmap e informes históricos por fase.
- `docs/sdk/api-contract.md` — contrato soportado del SDK.
- `frontend/admin/README.md` — uso del panel.
- `examples/astro-example/` — ejemplo SSR con el SDK propio.
