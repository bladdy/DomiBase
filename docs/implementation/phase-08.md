# Fase 8 — Preparación de la imagen de la API — completada

## Objetivo
Preparar el empaquetado de la futura imagen `bladdy34/domibase-api:0.1.0` con un Dockerfile multi-stage reproducible, usuario no root, healthcheck y contexto de build limpio. **La imagen no se ha construido ni publicado.**

## Entregado
- `infrastructure/docker/Dockerfile` — multi-stage (build + runtime) con las bases **fijadas por digest**.
- `infrastructure/docker/README.md` — diseño, variables de entorno requeridas, comandos de construcción documentados (no ejecutados) y pendientes de despliegue.
- `.dockerignore` raíz actualizado: excluye además `backend/tests/`, de modo que el contexto solo lleva el código necesario para compilar.

## Decisiones
- **Base `10.0-noble` (Ubuntu 24.04 LTS)** para `sdk` y `aspnet`, coherente con el destino Ubuntu. En .NET 10 ya **no existen** los tags `bookworm-slim` ni `trixie-slim`; se comprobó consultando el registro.
- **Digests fijados** en el `FROM` para que la construcción sea reproducible:
  - `sdk:10.0-noble@sha256:fe0fd4e9…`
  - `aspnet:10.0-noble@sha256:bfd912c8…`
- **Restore cacheado:** primero se copian los `.csproj` y se ejecuta `dotnet restore`; el código se copia después.
- **Publicación Release** con `--no-restore`, `UseAppHost=false` y sin símbolos.
- **Usuario no root `app`** (UID 1654) de las imágenes oficiales de .NET.
- **Healthcheck HTTP** sobre `GET /health` (liveness, sin dependencia de PostgreSQL); `curl` se instala solo en la etapa de runtime y se eliminan los índices de `apt`. Readiness separado en `/health/ready`.
- **Puerto interno `8080`** (`ASPNETCORE_URLS=http://+:8080`); TLS en el proxy externo del despliegue.
- **Sin secretos en la imagen:** `CONTROL_DB_CONNECTION_STRING`, `PROVISIONING_DB_CONNECTION_STRING`, `DOMIBASE_ADMIN_TOKEN`, `DOMIBASE_API_KEY_PEPPER` y `DOMIBASE_SECRETS_KEY` se inyectan en runtime. `ASPNETCORE_ENVIRONMENT` queda en Production por defecto.
- No se añadió un compose para la API: durante el desarrollo solo PostgreSQL corre en Docker.

## Verificación ejecutada

### Tags de imagen base (solo consulta de manifiestos)
```powershell
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0-noble --format '{{.Manifest.Digest}}'
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0-noble --format '{{.Manifest.Digest}}'
```
`10.0-noble` y `10.0-alpine` existen; `10.0-bookworm-slim` y `10.0-trixie-slim` devolvieron `not found`.

### Check estático del Dockerfile (no construye la imagen)
```powershell
docker buildx build --check -f infrastructure/docker/Dockerfile .
```
Resultado: **`Check complete, no warnings found.`** Solo se cargaron los metadatos de las imágenes base; no se ejecutó ningún stage.

### Publicación local con los mismos flags (Windows, fuera de Docker)
```powershell
dotnet publish backend/src/DomiBase.Api/DomiBase.Api.csproj `
  --configuration Release --output artifacts/publish-check `
  -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false
```
Resultado: compilación correcta de `DomiBase.Domain`, `DomiBase.Application`, `DomiBase.Provisioning`, `DomiBase.Infrastructure` y `DomiBase.Api`. El directorio de salida se eliminó tras la prueba.

### Sin imagen construida
No se ejecutó `docker build` ni `docker push`; no existe ninguna imagen `domibase-api` local ni publicada.

## Limitaciones / pendientes
- La imagen está preparada pero **no probada en contenedor**: el healthcheck, el arranque con variables reales y la conexión al PostgreSQL de despliegue requieren una construcción autorizada.
- Antes del despliegue: inventario del homelab, reducción de la credencial de provisioning a `CREATEDB`/`CREATEROLE`, gestión/backup de `DOMIBASE_SECRETS_KEY` y `DOMIBASE_API_KEY_PEPPER`, `RUNTIME_DB_SSL_MODE` endurecido y TLS en el proxy externo.
- Los digests deberán actualizarse de forma deliberada al revisar nuevas versiones de las bases.

## Fin de la Fase 1 (Database MVP)
Con esta fase quedan cubiertos los 13 puntos del alcance de Fase 1: inspección, documentación, solución .NET, PostgreSQL en Docker, Control Plane, provisioning, API keys, Runtime CRUD con RLS, SDK TypeScript, ejemplo Astro, tests automatizados y preparación del Dockerfile. El criterio de aceptación (`paneles-solares` con `contactos`, inserción desde Astro con el SDK propio y aislamiento frente a otro proyecto y a un visitante sin permiso) está verificado en las Fases 6 y 7.
