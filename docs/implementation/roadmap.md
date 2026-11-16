# Roadmap de implementación

## Fase 0 — Inspección y diseño
- Inspeccionar repositorio y git status.
- Inventariar versiones, servicios, puertos y redes.
- Confirmar estructura y convenciones existentes.
- Actualizar referencias NexStack a DomiBase si las hay.
- Revisar decisiones de seguridad y provisioning.
- Entregar plan antes de tocar infraestructura.

## Fase 1 — Database MVP
- Solución .NET y proyectos por capa.
- PostgreSQL + base `domibase_control`.
- Control API.
- Provisioning idempotente de bases por proyecto.
- API keys públicas.
- Runtime API CRUD.
- Roles PostgreSQL y RLS.
- SDK TypeScript.
- Panel privado mínimo.
- Ejemplo Astro.
- Pruebas unitarias, integración y seguridad.

## Fase 2 — Auth
- Usuarios por proyecto.
- Registro, login, logout, refresh.
- Hash de contraseñas Argon2id.
- JWT asimétrico.
- Refresh tokens rotatorios y revocables.
- Contexto de usuario y RLS autenticado.

## Fase 3 — Realtime
- SignalR.
- Suscripciones por proyecto/tabla.
- Autorización de canales y eventos.
- Entrega de eventos después de commit.

## Fase 4 — Storage
- Buckets.
- Metadatos y permisos.
- Volumen persistente.
- Upload/download/delete/list.
- URLs firmadas y límites.

## Fase 5 — Functions
- Definir modelo de ejecución.
- Aislamiento de procesos.
- Límites de CPU, memoria, tiempo y red.
- No ejecutar código arbitrario dentro del Runtime API.

## Condición de avance
Una fase no se marca terminada sin build, pruebas relevantes, documentación y criterios de aceptación cumplidos.
