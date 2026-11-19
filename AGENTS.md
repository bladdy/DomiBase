# DomiBase — instrucciones para agentes de desarrollo

## Misión
Construir DomiBase: una plataforma backend privada, administrada por un único propietario, con APIs y SDK propios. No reutilizar `@supabase/supabase-js`, GoTrue, PostgREST ni implementar el producto como una capa de configuración de Supabase. La experiencia puede inspirarse en patrones conocidos, pero el código, los contratos, la autenticación y la ejecución deben ser propios.

## Decisiones fijas
- Nombre del producto: DomiBase (nombre único; no hay migración NexStack pendiente).
- Backend: ASP.NET Core 10 / C#.
- Proceso: **un único host ASP.NET Core** (modular monolith) con dos áreas lógicas estrictamente separadas: **Control Plane** (privada) y **Runtime Plane** (pública de datos). No crear dos APIs independientes en Fase 1; la separación de módulos debe permitir una futura extracción sin rediseñar contratos.
- SDK: TypeScript independiente, paquete provisional `@domibase/client`.
- Panel privado futuro: React + Vite + TypeScript.
- Base de datos: PostgreSQL 18, imagen oficial con tag fijo (`postgres:18-alpine` validada), un servidor compartido y una base independiente por proyecto.
- Acceso PostgreSQL: Npgsql; EF Core + Npgsql para metadatos de control; Npgsql directo para consultas dinámicas del Runtime.
- Realtime futuro: SignalR.
- Desarrollo local: Windows. **Solo PostgreSQL se ejecuta en Docker Desktop**; API, SDK, tests y ejemplo Astro se ejecutan directamente en Windows (`dotnet run`, `npm`, `node`).
- Despliegue objetivo: Ubuntu 26.04.1 LTS, Docker Engine y Portainer. Proxy externo: Nginx Proxy Manager existente.
- Imagen futura de la API: `bladdy34/domibase-api` (preparar Dockerfile multi-stage sin construir hasta completar pruebas y autorización).
- Un servidor PostgreSQL compartido y una base independiente por proyecto. No crear un contenedor PostgreSQL por proyecto.
- Solo el propietario administra DomiBase. Los clientes finales no entran al panel.
- No implementar billing, planes, cuotas comerciales, registro de clientes ni organizaciones de clientes.

## Regla de trabajo: primero inspeccionar, luego planificar
Antes de cambiar archivos:
1. Inspecciona el repositorio, git status, herramientas instaladas y estructura existente.
2. No sobrescribas archivos existentes sin revisarlos.
3. No ejecutes comandos destructivos ni despliegues en el homelab sin aprobación explícita.
4. Detecta si el repositorio ya tiene decisiones/implementaciones que deban conservarse.
5. Entrega un diagnóstico y plan de cambios antes de modificar código.

Trabajar por fases y verificar cada entrega. Actualizar la documentación con cada cambio relevante. No declarar terminada una funcionalidad sin ejecutar sus pruebas.

## Arquitectura
Un único host ASP.NET Core 10 con dos áreas lógicas separadas:

- **Control Plane:** administración, metadatos, provisioning, claves y auditoría. Endpoints privados bajo `/api/v1`. Solo el propietario accede.
- **Runtime Plane:** peticiones de aplicaciones, resolución de proyecto por API key pública, API de datos bajo `/rest/v1`, permisos y políticas RLS. Nunca expone administración.

Ambas áreas comparten proceso, pero separan responsabilidades, servicios, endpoints, autenticación y conexiones. Evitar microservicios, Kubernetes y Redis al inicio.

Durante el desarrollo, el único componente que se ejecuta en Docker es PostgreSQL. No construir ni ejecutar una imagen Docker de la API hasta completar el desarrollo y superar las pruebas.

## Estructura objetivo
- `backend/DomiBase.sln`
- `backend/src/DomiBase.Api` (host único; áreas Control y Runtime separadas por módulos/carpetas)
- `backend/src/DomiBase.Domain`
- `backend/src/DomiBase.Application`
- `backend/src/DomiBase.Infrastructure`
- `backend/src/DomiBase.Provisioning`
- `backend/tests/DomiBase.UnitTests`
- `backend/tests/DomiBase.IntegrationTests`
- `backend/tests/DomiBase.SecurityTests`
- `packages/client`
- `packages/types` (solo si aporta valor real)
- `examples/astro-example`
- `frontend/admin` (posterior)
- `docs/architecture`
- `docs/sdk`
- `docs/implementation`
- `infrastructure/docker` (Dockerfile futuro)
- `infrastructure/postgres` (scripts SQL de roles/RLS)
- `infrastructure/nginx` (solo si aplica en despliegue)

No crees carpetas ni proyectos vacíos solo para aparentar avance. Ajusta la estructura si el repositorio existente justifica una mejora y documenta la decisión.

## Control Plane
Responsable de:
- Crear, consultar y eliminar proyectos.
- Provisionar una base de datos independiente por proyecto.
- Crear roles PostgreSQL restringidos.
- Generar, listar, revocar y rotar API keys.
- Guardar metadatos y configuración.
- Crear tablas/columnas/índices/políticas mediante operaciones administrativas validadas (incremento posterior al núcleo del MVP).
- Mantener estados `Provisioning`, `Ready`, `Failed`, `Deleting`.
- Registrar auditoría y errores de provisioning.

La base `domibase_control` contiene metadatos y nunca debe ser accesible desde claves públicas del Runtime ni desde el SDK público.

El provisioning debe ser idempotente, reintentable y compensable. `CREATE DATABASE` no puede ejecutarse dentro de una transacción PostgreSQL ordinaria: diseñar explícitamente el flujo de pasos y recuperación. La conexión administrativa/privilegiada queda restringida al proceso de provisioning.

## Runtime Plane
Responsable de:
- Resolver proyecto mediante API key.
- Validar estado `Ready`.
- Resolver conexión de proyecto de forma segura.
- Ejecutar CRUD sobre tablas expuestas.
- Validar tablas, columnas, filtros, operadores y ordenamientos.
- Usar SQL parametrizado para todos los valores.
- Usar allowlists y quoting seguro para identificadores SQL.
- Ejecutar transacciones cortas.
- Aplicar rol PostgreSQL y contexto RLS por transacción.
- Aplicar rate limits y límites de payload.
- Devolver errores estandarizados sin filtrar secretos ni SQL interno.

Nunca conectar como PostgreSQL superuser, propietario de tablas ni rol `BYPASSRLS`. Nunca aceptar el rol SQL desde el cliente. La identidad/claims deben derivarse de credenciales verificadas por el servidor. No permitir SQL arbitrario desde el Runtime ni endpoints públicos para ejecutar SQL.

## Seguridad PostgreSQL
Cada proyecto tendrá:
- Base de datos independiente (`domibase_<slug>`).
- Rol propietario/migraciones (`db_owner`) no usado por Runtime.
- Login técnico de Runtime (`db_runtime`) con privilegios mínimos.
- Roles funcionales `anon` y `authenticated` (este último reservado para fase posterior) según se habiliten.
- `GRANT` mínimos por tabla y operación.
- RLS habilitado en tablas expuestas.
- Políticas RLS específicas.

Para fase 1, soportar acceso anónimo limitado y demostrar políticas de INSERT/SELECT. El Runtime debe iniciar una transacción, ejecutar `SET LOCAL ROLE` a un rol permitido y establecer claims de request con `set_config(..., true)`. El contexto debe desaparecer al terminar la transacción. Probar que el pool de conexiones no filtra identidad/rol entre peticiones.

## API HTTP
Sistema:
- `GET /health`
- `GET /health/ready`

Control API privada (mecanismo administrativo separado de las claves públicas):
- `GET /api/v1/projects`
- `GET /api/v1/projects/{id}`
- `POST /api/v1/projects`
- `POST /api/v1/projects/{id}/keys`
- `DELETE /api/v1/projects/{id}/keys/{keyId}`

Runtime API pública de datos (proyecto resuelto server-side por API key pública):
- `GET /rest/v1/{table}`
- `POST /rest/v1/{table}`
- `PATCH /rest/v1/{table}`
- `DELETE /rest/v1/{table}`

Filtros, orden, límites y selección de columnas se validan server-side contra allowlists y parámetros permitidos. `PATCH`/`DELETE` requieren filtros acotados. No exponer endpoints de administración en Runtime. Proteger Control API con autenticación administrativa y autorización. No asumir que CORS es seguridad.

## API Keys
- Clave pública con prefijo `db_public_`.
- Clave secreta futura con prefijo `db_secret_`; solo para servidores.
- Generar valores aleatorios criptográficamente seguros.
- Mostrar el valor completo solo al crear/rotar.
- Guardar hash verificable (HMAC-SHA-256 con pepper del servidor o diseño equivalente) y nunca el valor original.
- Soportar revocación y rotación.
- La clave pública solo identifica el proyecto; no concede por sí sola lectura/escritura de datos.

## SDK TypeScript
Paquete provisional `@domibase/client`.
No instalar ni importar paquetes Supabase. No copiar su implementación.

API inicial:
```ts
import { createClient } from "@domibase/client";

const db = createClient({
  url: "http://localhost:5080",
  projectKey: "db_public_xxx",
});

const { data, error } = await db
  .from("contactos")
  .select("id,nombre,email")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(20);
```

Métodos fase 1:
- `createClient`
- `from`
- `select`
- `insert`
- `update`
- `delete`
- `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`
- `order`, `limit`, `range`
- `single`, `maybeSingle` solo si su semántica queda probada y documentada.

Resultado:
`{ data, error, count? }`

Error:
`{ code, message, details?, hint?, status? }`

Separar transporte HTTP, query builder, serialización, tipos y errores. Soportar fetch inyectable, AbortSignal, timeout, SSR/Node/browser y no acceder a `window` durante import. No implementar Auth/Realtime/Storage como mocks en fase 1.

## Pruebas obligatorias
- Unit tests de query builder y serialización.
- Integration tests contra PostgreSQL real en Docker (no SQLite ni mocks para comportamiento PostgreSQL).
- Security tests de aislamiento entre proyectos.
- API key inválida/revocada.
- Acceso a tabla no expuesta.
- SQL injection en valores e identificadores.
- RLS deniega SELECT público cuando no hay política.
- RLS permite solo el INSERT configurado.
- Aislamiento de contexto entre conexiones pooled.
- Reinicio de contenedores conserva datos.

## Homelab y operaciones
- No asumir puertos libres: en desarrollo el puerto local `5432` está ocupado por otro stack; DomiBase usa `127.0.0.1:5433` para PostgreSQL (evitando rangos reservados por Windows/Hyper-V).
- No tocar ni reemplazar stacks existentes.
- Durante el desarrollo, solo PostgreSQL se ejecuta en Docker; no API, SDK, tests ni Astro en contenedores.
- No ejecutar `docker compose down -v` ni eliminar volúmenes/recursos existentes.
- PostgreSQL no debe publicarse en interfaces públicas (solo `127.0.0.1` en desarrollo).
- Usar volúmenes persistentes y health checks.
- Configurar límites de memoria/CPU solo después de medir.
- Evitar Redis y Kubernetes inicialmente.
- Logs con rotación y redacción de secretos.
- Mantener `.env.example` sin secretos reales.
- No copiar archivos `.env` al repositorio.
- No ejecutar commits ni pushes automáticamente.

## Fase 1 (alcance actual)
Implementar únicamente:
1. Inspección del repositorio y entorno.
2. Documentación técnica y plan.
3. Solución .NET y proyectos por capa con responsabilidad real.
4. Docker Compose de desarrollo aislado que levanta **solo PostgreSQL**.
5. PostgreSQL (`domibase_control`) y verificación de conexión/persistencia.
6. Control Plane y metadatos.
7. Provisioning de bases por proyecto (idempotente y recuperable).
8. API keys públicas.
9. Runtime CRUD con RLS y aislamiento.
10. SDK TypeScript.
11. Ejemplo Astro.
12. Tests automatizados (unitarios, integración y seguridad).
13. Preparación de Dockerfile para imagen futura (sin construir ni publicar).

Criterio de aceptación: crear `paneles-solares`, provisionar su base, crear `contactos`, insertar un contacto desde Astro con el SDK propio y demostrar que otro proyecto y un visitante sin permiso no pueden leer esos datos. PostgreSQL funciona solo en Docker; API y SDK funcionan directamente en Windows.
