# DomiBase

Plataforma backend privada con APIs y SDK TypeScript propios, construida con ASP.NET Core y PostgreSQL.

## Objetivo
Administrar proyectos backend independientes para aplicaciones Astro, React y otras aplicaciones. El propietario administra DomiBase; los clientes finales solo utilizan las aplicaciones construidas para ellos.

## Tecnologías
- ASP.NET Core 10 / C#
- PostgreSQL
- Npgsql y EF Core
- TypeScript SDK (`@domibase/client`, nombre provisional)
- React + Vite para el panel privado
- SignalR para Realtime futuro
- Docker Compose / Portainer

## Arquitectura
- **Control Plane:** proyectos, metadatos, provisioning, claves y auditoría.
- **Runtime Plane:** API pública de datos y autorización.
- **PostgreSQL:** servidor compartido con una base de datos independiente por proyecto.

## Estado
En fase de especificación e inspección. No desplegar en el homelab hasta revisar puertos, redes, volúmenes y servicios existentes.

## Principio
DomiBase es una implementación propia. No depende de `@supabase/supabase-js`, GoTrue ni PostgREST.
