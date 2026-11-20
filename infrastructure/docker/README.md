# Imagen futura de la API — `infrastructure/docker/Dockerfile`

Estado: **construida localmente para pruebas, no publicada**. El stack `docker compose` de la raíz levanta PostgreSQL, esta API y el panel admin bajo el proyecto `domibase`; el modo clásico sigue permitiendo ejecutar la API en Windows con `dotnet run`. Este directorio contiene el artefacto de empaquetado para el despliegue futuro (`bladdy34/domibase-api:0.1.0`).

## Diseño

- **Multi-stage:** `mcr.microsoft.com/dotnet/sdk:10.0-noble` para compilar y `mcr.microsoft.com/dotnet/aspnet:10.0-noble` para ejecutar, ambos **fijados por digest** (Ubuntu 24.04 LTS, coherente con el destino Ubuntu). Nota: en .NET 10 ya no existen los tags `bookworm-slim` ni `trixie-slim`.
- **Cache de restore:** primero se copian los `.csproj` y se ejecuta `dotnet restore`; el código se copia después.
- **Publicación Release** con `--no-restore`, `UseAppHost=false` y sin símbolos de depuración.
- **Usuario no root:** usuario `app` (UID 1654) incluido en las imágenes oficiales de .NET.
- **Healthcheck HTTP** sobre `GET /health` (liveness, no depende de PostgreSQL). La sonda de readiness es `GET /health/ready`.
- **Puerto interno `8080`** (`ASPNETCORE_URLS=http://+:8080`). El proxy externo (Nginx Proxy Manager) terminará TLS en el despliegue.
- **`ASPNETCORE_ENVIRONMENT` no se fija en la imagen** (Production por defecto); OpenAPI solo se publica en Development.
- El `HEALTHCHECK` instala `curl` solo en la etapa de runtime y elimina los índices de `apt`.

## Contexto de build y `.dockerignore`

El contexto de build debe ser la **raíz del repositorio**. El `.dockerignore` raíz excluye `.git`, `bin/`, `obj/`, `node_modules/`, `dist/`, `docs/`, `examples/`, `frontend/`, `packages/`, `infrastructure/` y `backend/tests/`, además de todos los archivos `.env`.

Por tanto, la imagen solo recibe el código de `backend/src` necesario para compilar; no recibe secretos, tests, SDK, ejemplo Astro ni este propio directorio.

## Configuración requerida en runtime

Ningún secreto se incluye en la imagen. El contenedor debe recibir, como variables de entorno (o por el gestor de secretos del despliegue):

| Variable | Obligatoria | Notas |
|---|---|---|
| `CONTROL_DB_CONNECTION_STRING` | Sí | Solo metadatos de `domibase_control`. |
| `PROVISIONING_DB_CONNECTION_STRING` | Sí | Solo provisioning; credencial administrativa restringida. |
| `DOMIBASE_ADMIN_TOKEN` | Sí | Mínimo 16 caracteres; autenticación del Control Plane. |
| `DOMIBASE_API_KEY_PEPPER` | Sí | Mínimo 16 caracteres; cambiarlo invalida los hashes existentes. |
| `DOMIBASE_SECRETS_KEY` | Sí | Base64 de exactamente 32 bytes (AES-256-GCM). |
| `RUNTIME_DB_HOST` / `RUNTIME_DB_PORT` | No | Host/puerto de PostgreSQL en la red de despliegue. |
| `RUNTIME_DB_SSL_MODE` | No | Endurecer en despliegue (no `Prefer`). |
| `RUNTIME_RATE_LIMIT_PERMITS`, `RUNTIME_RATE_LIMIT_WINDOW_SECONDS` | No | Límites del Runtime. |
| `DOMIBASE_CORS_ORIGINS` | No | Orígenes separados por comas; CORS no es autorización. |
| `ASPNETCORE_URLS` | No | Ya definida como `http://+:8080`. |

La API carga `.env` **solo en desarrollo**; en el contenedor se deben usar variables de entorno reales, no un archivo `.env` montado con secretos. En el stack local, Compose usa `env_file: .env` y sobrescribe las cadenas de conexión y `RUNTIME_DB_HOST` para apuntar a `postgres:5432` dentro de `domibase_net`.

## Construcción y publicación

La construcción local ya se ejecuta desde la raíz con `docker compose up -d --build`. La publicación de `bladdy34/domibase-api:0.1.0` **sigue sin autorizarse**.

```powershell
# Solo con autorización explícita:
# docker build -f infrastructure/docker/Dockerfile -t bladdy34/domibase-api:0.1.0 .
# docker push bladdy34/domibase-api:0.1.0
```

Validación estática ya ejecutada (no construye la imagen):

```powershell
docker buildx build --check -f infrastructure/docker/Dockerfile .
```

Las imágenes base están fijadas por digest; antes de una actualización deliberada se revisarán los nuevos digests y se repetirá este check.

## Pendientes de despliegue

- Inventario del homelab (contenedores, redes, puertos, volúmenes) antes de desplegar.
- Reducir la credencial de provisioning a `CREATEDB`/`CREATEROLE` + acceso a `domibase_control` (en local es superuser).
- Gestión y backup de `DOMIBASE_SECRETS_KEY` y `DOMIBASE_API_KEY_PEPPER`; su rotación no está implementada.
- Configurar `RUNTIME_DB_SSL_MODE` y TLS en el proxy externo.
- Límites de memoria/CPU solo después de medir.
