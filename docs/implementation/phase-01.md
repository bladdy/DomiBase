# Fase 1 — Database MVP

## Objetivo
Desde el panel privado, crear el proyecto `paneles-solares`, provisionar su base PostgreSQL, crear la tabla `contactos` y operar sobre ella desde una app Astro usando el SDK DomiBase.

## Orden de tareas

### 1. Inspección
- `git status`, árbol de archivos y archivos de solución existentes.
- Versiones de .NET SDK, Docker y Node.
- Inventario de contenedores, redes, puertos y volúmenes si el despliegue se considera.
- No tocar stacks existentes.

### 2. Diseño
- Confirmar contratos de Control API y Runtime API.
- Diseñar entidades de control: Project, ApiKey, ProvisioningOperation, ExposedTable, AuditEntry.
- Diseñar máquina de estados de provisioning.
- Diseñar roles, grants y RLS por proyecto.

### 3. Infraestructura local
- Compose aislado para desarrollo.
- PostgreSQL con volumen nombrado.
- Health check.
- No publicar PostgreSQL en interfaces externas.
- Variables en `.env`; solo `.env.example` versionado.

### 4. Control Plane
- Migraciones de `domibase_control`.
- CRUD administrativo de proyectos.
- Generación/hash/revocación de API keys.
- Provisioning idempotente y recuperable.
- Crear DB y roles de proyecto sin superuser en Runtime.

### 5. Runtime Plane
- Middleware de API key.
- Resolver proyecto y conexión.
- Endpoint query/insert/update/delete.
- Validar tablas/columnas expuestas.
- Parametrizar valores.
- Transacción y contexto RLS local.
- Respuestas/errores normalizados.

### 6. SDK
- `createClient`.
- Query builder.
- Transporte HTTP.
- Filtros, sort, limit y paginación.
- Tipos y errores.
- Tests de serialización y contrato.

### 7. Panel y ejemplo
- Login administrativo básico seguro.
- Crear proyecto.
- Ver estado y endpoint.
- Crear tabla/columnas iniciales.
- Ejemplo Astro que inserte y consulte con SDK.

### 8. Verificación
- Proyecto A no accede a Proyecto B.
- API key revocada falla.
- Tabla no expuesta falla.
- Visitante no puede SELECT si no hay policy.
- INSERT público solo si hay policy.
- Contexto RLS no se filtra entre conexiones pooled.
- Datos sobreviven a reinicio.

## Definition of Done
Build limpio, pruebas pasando, documentación actualizada, despliegue reproducible y ninguna credencial real en Git.
