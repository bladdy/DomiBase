# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Único propietario de DomiBase. Entra con usuario/contraseña configurados en el entorno y trabaja desde un panel privado y minimalista: crear proyectos, editar su cupo, eliminarlos, diseñar tablas y registros, y copiar la configuración del SDK. No hay clientes finales ni organizaciones en el panel.

## Product Purpose

DomiBase es una herramienta backend privada, administrada por una sola persona, con APIs y SDK propios. El panel es la consola operativa del Control Plane: convierte los endpoints `/api/v1` en una interfaz segura y auditable sin exponer administración en el Runtime Plane. El éxito es crear un proyecto con PostgreSQL propio, modelar sus tablas, insertar registros y conectar una aplicación Astro con el SDK, sin scripts ad-hoc ni secretos administrativos en el navegador.

## Positioning

Un único host ASP.NET Core con Control Plane y Runtime Plane separados; PostgreSQL compartido con una base independiente por proyecto; SDK TypeScript propio. El panel consume la Control API con una sesión firmada y caducable obtenida mediante login; el token permanente de servicio queda reservado a automatizaciones y tests.

## Operating Context

Desarrollo local en Windows: solo PostgreSQL corre en Docker en `127.0.0.1:5433`; la API, el panel, el SDK, el ejemplo y los tests corren directamente. Objetivo posterior: Ubuntu con Docker Engine y proxy externo. No se construyen imágenes ni se despliega al homelab sin aprobación explícita.

## Capabilities and Constraints

- **Login:** credencial única configurable (`DOMIBASE_ADMIN_USERNAME` / `DOMIBASE_ADMIN_PASSWORD`), canjeada por `POST /admin/v1/login` por una sesión `dbsess_` firmada HMAC-SHA-256 y caducable; `DOMIBASE_ADMIN_TOKEN` solo para servicios/tests.
- **Proyectos:** crear, seleccionar, reintentar provisioning si falla, editar cupo de Runtime y eliminar con confirmación por slug.
- **Resumen:** estado real de provisioning, base asignada, fecha, cupo efectivo y motivo de fallo.
- **Table Editor:** crear/eliminar tablas; columnas con tipos PostgreSQL allowlisted, PK/FK/NOT NULL/UNIQUE/DEFAULT/IDENTITY; crear/eliminar columnas; activar políticas RLS mínimas de SELECT/INSERT para `anon`; CRUD de registros con buscar/filtrar/ordenar/paginar.
- **Conexión:** URL de la API, clave pública (visible completa solo al crearla), metadatos PostgreSQL no secretos, configuración JSON y ejemplo SDK.
- **Navegación:** global solo Proyectos; dentro de proyecto solo Resumen, Table Editor, Conexión y Volver a proyectos.
- Retirados de UI/navegación y documentación activa: Auth de usuarios finales, Storage, Realtime, Functions, Advisors, Observability, Integrations, Branching, SQL Editor, migraciones, backups, auditoría, buckets, organizaciones, facturación, planes y clientes SaaS. El código backend/shared de esas capacidades se conserva mientras no afecte al alcance.
- Las escrituras del editor pasan por Control Plane; la clave pública nunca se expone en el navegador salvo la que el propio propietario crea en la pantalla Conexión.
- Sesión en `sessionStorage`; nunca en `localStorage`, URL ni bundle. CORS no es autorización.
- React + Vite + TypeScript estricto en `frontend/admin`.

## Brand Commitments

Nombre del producto: DomiBase. Sin migración NexStack pendiente. Sin billing, planes, cuotas comerciales, registro de clientes ni organizaciones.

Inspiración visual de consola (raíl oscuro, tablas densas y acento verde) únicamente: sin dependencias, contratos, SDK, GoTrue ni PostgREST de Supabase.

## Evidence on Hand

`AGENTS.md`, `docs/architecture/*`, `docs/implementation/admin-minimal-scope.md` y la Control API existente en `backend/src/DomiBase.Api/Endpoints/`. No hay assets visuales ni identidad gráfica previa.

## Product Principles

1. Separación estricta entre administración y datos públicos.
2. Secretos efímeros y visibles solo en el momento de creación.
3. Operaciones explícitas y verificables: estado, error y reintento siempre a la vista.
4. Sin magia: el panel consume contratos HTTP reales y documentados.
5. Superficie mínima: cada pantalla y cada export del SDK deben justificar su existencia.
