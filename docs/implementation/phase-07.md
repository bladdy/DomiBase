# Fase 7 — Validación final — completada

## Objetivo
Confirmar, contra el stack local real (PostgreSQL 18 en Docker + API + SDK), que los datos sobreviven un reinicio del contenedor, que el aislamiento y la seguridad siguen activos después del reinicio, y que el repositorio no filtra secretos. No se modificó código de backend ni del Runtime.

## Verificaciones ejecutadas

### Suites automatizadas
```powershell
dotnet test backend/DomiBase.sln
cmd /c npm test   # en packages/client
```
- Backend: **98 tests en verde** — 73 unitarios, 19 de integración contra PostgreSQL real y 6 de seguridad.
- SDK: **50 tests en verde** con `node --test`.
- La integración/seguridad existentes cubren: CRUD, inserción masiva, filtros/orden, validaciones, límite de payload, rate limiter, claves ausente/inválida/revocada, tabla sin `GRANT`, tabla sin RLS, RLS sin política (SELECT vacío e INSERT 403), aislamiento de filas por claim `domibase.project_id`, aislamiento entre dos proyectos reales, inyección SQL en valores/`select`/tabla, y ausencia de fugas de rol/claims en conexiones pooled.

### Persistencia tras reinicio del contenedor
Secuencia no destructiva (`stop` + `up`, sin tocar volúmenes):
```powershell
docker compose stop
docker compose up -d
```
Tras el reinicio, `domibase-postgres` quedó `Up (healthy)` en `127.0.0.1:5433`.

Estado antes y después (mismos valores):

| Recurso | Antes | Después |
|---|---|---|
| `domibase_control.projects` | 2 | 2 |
| `domibase_control.api_keys` | 3 | 3 |
| `provisioning_operations` | 6 | 6 |
| `domibase_paneles_solares.public.contactos` | `efb4ae18-…` \| `contacto@paneles-solares.test` | idéntico (mismo id) |
| `domibase_aislamiento_demo.public.contactos` | `60fea0be-…` \| `contacto@aislamiento-demo.test` | idéntico (mismo id) |
| `domibase_smoke_check.public.persistence_check` | `1\|ok` | `1\|ok` |

### Validación post-reinicio con la API real
`scripts/dev/phase-07-validate.mjs` (nuevo, **de solo lectura**: no crea, rota ni revoca claves) verificó contra `http://localhost:5080`:

```
Fase 7 · validación post-reinicio
  [OK] API lista en http://localhost:5080
  [OK] Aislamiento del proyecto principal (ve su fila, no la ajena)
  [OK] Aislamiento bidireccional entre proyectos
  [OK] domibase_control bloqueado desde el Runtime (404 TABLE_NOT_FOUND)
  [OK] Clave inválida, clave revocada y clave ausente rechazadas con 401
  [OK] Bases y roles presentes (4 bases, 7 roles domibase_*)
  [OK] Persistencia de filas verificada (1 y 1 filas semilla)

Comprobaciones superadas: 7 · fallos: 0
```

El script comprueba además que existen las bases `domibase_control`, `domibase_paneles_solares` y `domibase_aislamiento_demo`, y los roles `_owner`, `_runtime` y `_anon` de ambos proyectos.

### Revisión de seguridad
- `docs/architecture/security.md` revisado: las propiedades implementadas (hash HMAC de claves, AES-256-GCM de contraseñas de Runtime, `SET ROLE` transaccional con claims locales, allowlists, `PATCH`/`DELETE` acotados, errores sin SQL/secretos, logs redactados) siguen vigentes y están cubiertas por tests.
- Barrido de secretos en archivos no ignorados (`*.md`, `*.mjs`, `*.ts`, `*.cs`, `*.json`, `*.yml`, `.env.example`, etc.): solo aparecen los **placeholders** de `.env.example`. No hay valores reales de `DOMIBASE_ADMIN_TOKEN`, `DOMIBASE_API_KEY_PEPPER`, `DOMIBASE_SECRETS_KEY`, `POSTGRES_PASSWORD`, `db_public_` ni `db_secret_`.
- `.env` (raíz y del ejemplo), `node_modules/`, `dist/` y `.astro/` confirmados como ignorados por Git.
- El ejemplo Astro no filtra `db_public_` en el HTML servido (verificado en Fase 6).

## Entregado
- `scripts/dev/phase-07-validate.mjs`: verificación de solo lectura de persistencia, aislamiento, seguridad de claves y recursos PostgreSQL tras un reinicio.
- Documentación actualizada: esta fase, `roadmap.md`, `local-setup.md`, `overview.md`, `phase-01.md` y `README.md`.

## Limitaciones / pendientes
- La persistencia se verificó con `stop`/`up` del contenedor; no se probó una recreación del contenedor ni un apagado anfitrión, que requerirían validación con autorización en el homelab.
- Histórico de esta fase: la cuota de rate limit por proyecto estaba pendiente; se resolvió en P1.4 (`docs/implementation/p1-4-rate-limits.md`). Sigue pendiente documentar la rotación de `DOMIBASE_SECRETS_KEY`.
- La validación no ejecuta el ejemplo Astro end-to-end (ya cubierto en Fase 6); aquí se valida la capa de datos y seguridad.

## Siguiente
Fase 8: preparación del Dockerfile multi-stage de la API (usuario no root, base fijada, healthcheck, `.dockerignore`) **sin construir ni publicar**.
