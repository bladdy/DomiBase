# Arquitectura general de DomiBase

## Objetivo
Construir una plataforma backend privada con SDK propio. La primera entrega prioriza un flujo completo y seguro: crear proyecto, provisionar base PostgreSQL, exponer CRUD limitado y consumirlo desde Astro.

## Planes separados

### Control Plane
Administra proyectos, metadatos, credenciales, esquemas, políticas y auditoría. Solo el propietario puede acceder.

### Runtime Plane
Atiende solicitudes de las aplicaciones. Identifica el proyecto mediante una API key pública, verifica su estado, aplica límites y ejecuta consultas bajo roles PostgreSQL restringidos.

## Despliegue inicial
Modular monolith con dos aplicaciones ASP.NET Core desplegables: Control API y Runtime API. PostgreSQL compartido. Panel React estático. Realtime y Storage se agregan después.

## Aislamiento
Una base de datos PostgreSQL por proyecto. El aislamiento de red no reemplaza el aislamiento de credenciales y permisos. Runtime no utiliza roles propietarios ni superuser.

## Provisioning
Flujo idempotente con estados Provisioning, Ready, Failed y Deleting. Crear base de datos fuera de una transacción PostgreSQL ordinaria. Cada paso debe poder verificarse y reintentarse sin duplicar recursos.

## Decisiones pendientes de la inspección
- Versiones exactas instaladas de Docker, .NET y PostgreSQL.
- Puertos, redes y volúmenes ya ocupados.
- Ubicación del repositorio y estructura existente.
- Estrategia de secretos y persistencia de claves de cifrado.
- Disponibilidad del scope npm `@domibase`.
