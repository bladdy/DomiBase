# DomiBase — instrucciones para agentes de desarrollo

## Misión
Construir DomiBase: una plataforma backend privada, administrada por un único propietario, con APIs y SDK propios. No reutilizar `@supabase/supabase-js`, GoTrue, PostgREST ni implementar el producto como una capa de configuración de Supabase. La experiencia puede inspirarse en patrones conocidos, pero el código, los contratos, la autenticación y la ejecución deben ser propios.

## Decisiones fijas
- Nombre del producto: DomiBase.
- Backend: ASP.NET Core 10 / C#.
- SDK: TypeScript independiente.
- Paquete provisional: `@domibase/client` (verificar disponibilidad del scope y nombre en npm antes de publicar).
- Panel privado: React + Vite + TypeScript.
- Base de datos: PostgreSQL 18, sujeto a validar imagen/compatibilidad del entorno.
- Acceso PostgreSQL: Npgsql; EF Core + Npgsql para metadatos de control.
- Realtime futuro: SignalR.
- Infraestructura: Docker Compose y Portainer.
- Proxy externo: Nginx Proxy Manager existente.
- Host objetivo: Ubuntu homelab con 16 GB RAM.
- Un servidor PostgreSQL compartido y una base independiente por proyecto.
- No crear un contenedor PostgreSQL por proyecto.
- Solo el propietario administra DomiBase. Los clientes finales no entran al panel.
- No implementar billing, planes, cuotas comerciales, registro de clientes ni organizaciones de clientes.

## Regla de trabajo: primero inspeccionar, luego planificar
Antes de cambiar archivos:
1. Inspecciona el repositorio, git status, herramientas instaladas y estructura existente.
2. No sobrescribas archivos existentes sin revisarlos.
3. No ejecutes comandos destructivos ni despliegues en el homelab sin aprobación explícita.
4. Detecta si el repositorio ya tiene decisiones/implementaciones que deban conservarse.
5. Entrega un diagnóstico y plan de cambios antes de modificar código.

## Arquitectura
Separar:
- Control Plane: administración, metadatos, provisioning, claves y auditoría.
- Runtime Plane: peticiones de aplicaciones, resolución de proyecto, API de datos y políticas.

Primera versión como modular monolith: dos aplicaciones ASP.NET Core desplegables (Control API y Runtime API), módulos internos separados y PostgreSQL compartido. Evitar microservicios, Kubernetes y Redis al inicio.

## Estructura objetivo
- `backend/src/DomiBase.Control.Api`
- `backend/src/DomiBase.Runtime.Api`
- `backend/src/DomiBase.Application`
- `backend/src/DomiBase.Domain`
- `backend/src/DomiBase.Infrastructure`
- `backend/src/DomiBase.Provisioning`
- `backend/src/DomiBase.Shared`
- `backend/tests/DomiBase.UnitTests`
- `backend/tests/DomiBase.IntegrationTests`
- `backend/tests/DomiBase.SecurityTests`
- `frontend/admin`
- `packages/client`
- `packages/react`
- `packages/types`
- `docs/architecture`
- `docs/sdk`
- `docs/implementation`
- `infrastructure/docker`
- `infrastructure/postgres`
- `infrastructure/nginx`
- `examples/astro-example`
- `examples/react-example`

No crees carpetas ni proyectos vacíos solo para aparentar avance. Ajusta la estructura si el repositorio existente justifica una mejora y documenta la decisión.

## Control Plane
Responsable de:
- Crear, consultar y eliminar proyectos.
- Provisionar una base de datos independiente por proyecto.
- Crear roles PostgreSQL restringidos.
- Generar, listar, revocar y rotar API keys.
- Guardar metadatos y configuración.
- Crear tablas/columnas/índices/políticas mediante operaciones administrativas validadas.
- Mantener estados `Provisioning`, `Ready`, `Failed`, `Deleting`.
- Registrar auditoría.

La base `domibase_control` contiene metadatos y nunca debe ser accesible desde claves públicas del Runtime.

El provisioning debe ser idempotente, reintentable y compensable. `CREATE DATABASE` no puede ejecutarse dentro de una transacción PostgreSQL ordinaria: diseñar explícitamente el flujo de pasos y recuperación.

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

Nunca conectar como PostgreSQL superuser, propietario de tablas ni rol `BYPASSRLS`. Nunca aceptar el rol SQL desde el cliente. La identidad/claims deben derivarse de credenciales verificadas por el servidor.

## Seguridad PostgreSQL
Cada proyecto tendrá:
- Base de datos independiente.
- Rol propietario/migraciones no usado por Runtime.
- Login de Runtime con privilegios mínimos.
- Roles funcionales `domibase_anon` y `domibase_authenticated` según se habiliten.
- `GRANT` mínimos por tabla y operación.
- RLS habilitado en tablas expuestas.
- Políticas RLS específicas.

Para fase 1, soportar acceso anónimo limitado y demostrar políticas de INSERT/SELECT. El Runtime debe iniciar una transacción, ejecutar `SET LOCAL ROLE` a un rol permitido y establecer claims de request con `set_config(..., true)`. El contexto debe desaparecer al terminar la transacción. Probar que el pool de conexiones no filtra identidad/rol entre peticiones.

## API pública inicial
Base `/v1`:
- `GET /health`
- `POST /data/{table}/query`
- `POST /data/{table}/insert`
- `PATCH /data/{table}`
- `DELETE /data/{table}`

Control API privada:
- `/v1/projects`
- `/v1/projects/{id}`
- `/v1/projects/{id}/keys`
- `/v1/projects/{id}/tables`
- `/v1/projects/{id}/policies`

No exponer endpoints de administración en Runtime. Proteger Control API con autenticación administrativa y autorización. No asumir que CORS es seguridad.

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
const db = createClient({
  url: "https://api.example.com",
  projectKey: "db_public_xxx",
});

const result = await db
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

Separar transporte HTTP, query builder, serialización, tipos y errores. Soportar fetch inyectable, AbortSignal, timeout, SSR/Node/browser y no acceder a `window` durante import. No implementar Auth/Reatime/Storage como mocks en fase 1.

## Pruebas obligatorias
- Unit tests de query builder y serialización.
- Integration tests contra PostgreSQL real en contenedor.
- Security tests de aislamiento entre proyectos.
- API key inválida/revocada.
- Acceso a tabla no expuesta.
- SQL injection en valores e identificadores.
- RLS deniega SELECT público cuando no hay política.
- RLS permite solo el INSERT configurado.
- Aislamiento de contexto entre conexiones pooled.
- Reinicio de contenedores conserva datos.

## Homelab y operaciones
- No asumir puertos libres.
- No tocar ni reemplazar stacks existentes.
- PostgreSQL no debe publicarse en interfaces públicas.
- Usar volúmenes persistentes y health checks.
- Configurar límites de memoria/CPU solo después de medir.
- Evitar Redis y Kubernetes inicialmente.
- Logs con rotación y redacción de secretos.
- Mantener `.env.example` sin secretos reales.
- No copiar archivos `.env` al repositorio.

## Fase 1
Implementar únicamente:
1. Inspección del repositorio y entorno.
2. Documentación técnica y plan.
3. Monorepo/solución .NET según corresponda.
4. Docker Compose de desarrollo aislado.
5. PostgreSQL y base `domibase_control`.
6. Control API y metadatos.
7. Provisioning de bases por proyecto.
8. API keys públicas.
9. Runtime CRUD.
10. Roles y RLS básico.
11. SDK TypeScript.
12. Panel mínimo para crear proyectos/tablas.
13. Ejemplo Astro.
14. Tests automatizados.

Criterio de aceptación: crear `paneles-solares`, provisionar su base, crear `contactos`, insertar un contacto desde Astro con el SDK propio y demostrar que otro proyecto y un visitante sin permiso no pueden leer esos datos.

## Primera tarea de OpenCode
No programes inmediatamente. Inspecciona el repositorio y el entorno, actualiza los nombres NexStack a DomiBase si existen, identifica riesgos de colisión con servicios actuales y prepara el plan detallado. Presenta los hallazgos antes de comenzar implementación.
