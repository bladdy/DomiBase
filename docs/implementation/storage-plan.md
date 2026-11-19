# Fase 4 de producto — Storage — plan y decisiones

> **Estado (2026-11-17): implementada y verificada.** Backend, SDK y demo Astro completos.
> 152 tests backend en verde (97 unitarios + 49 integración + 6 seguridad; 11 de integración
> de Storage) y 66 tests del SDK en verde. La demo de Astro compila (`npm run build`).

## Objetivo
Almacenamiento de objetos por proyecto: buckets, metadatos en `domibase_control`, archivos en filesystem local, permisos básicos y URLs firmadas.

## Decisiones de diseño

### Almacenamiento
- Fase 4 MVP usa **filesystem local**. Ruta configurable mediante `STORAGE_ROOT` (default `./storage` en desarrollo).
- Layout físico: `{STORAGE_ROOT}/{projectId}/{bucket}/{objectPath}`.
- Límite de tamaño por objeto: configurable (`STORAGE_MAX_OBJECT_SIZE_BYTES`, default 10 MB).
- Tipos MIME detectados por extensión; el cliente puede sobreescribir con cabecera `Content-Type`.

### Metadatos
- `StorageBucket` en `domibase_control`:
  - `Id`, `ProjectId`, `Name`, `PublicAccess`, `CreatedAt`, `UpdatedAt`.
  - Nombre de bucket: `[a-z0-9-]{3,40}`.
- `StorageObject` en `domibase_control`:
  - `Id`, `BucketId`, `Path`, `ContentType`, `Size`, `Etag`, `CreatedAt`, `UpdatedAt`.
  - `(BucketId, Path)` único.

### Permisos
- Bucket público (`PublicAccess = true`):
  - `anon`: lectura (download/list).
  - `authenticated`: lectura/escritura/eliminación.
- Bucket privado (`PublicAccess = false`):
  - `anon`: ningún acceso.
  - `authenticated`: lectura/escritura/eliminación.
- En Fase 4 MVP no hay políticas RLS por objeto; la autorización es por bucket.
- Las URLs firmadas cuentan como autorización de lectura: son emitidas por el servidor solo
  para usuarios autenticados y permiten descargar incluso de buckets privados.

### Decisiones de seguridad adicionales
- La firma HMAC incluye `projectId`, `bucket`, `path` y `expires` (`{projectId:N}:{bucket}:{path}:{expires}`).
  Así una URL firmada para un proyecto no sirve en otro aunque coincidan bucket y ruta (test de aislamiento incluido).
- La validación de la firma usa `CryptographicOperations.FixedTimeEquals` (comparación en tiempo constante).
- El upload hace streaming con límite de tamaño (`CopyWithLimitAsync`) y escribe primero en un
  archivo temporal, moviéndolo al destino solo al completar; no se usa `stream.Length` (los bodies HTTP
  no lo exponen de forma fiable).
- Las rutas de objeto se normalizan (`\`→`/`, sin `..`, sin `/` inicial) y se validan de nuevo en el
  backend filesystem resolver antes de tocar disco (protección contra path traversal).
- Los endpoints Runtime de Storage devuelven el envelope `{ data }` para ser consistentes con el SDK.

### Endpoints Control Plane (`/api/v1`)
- `GET /api/v1/projects/{id}/buckets`
- `POST /api/v1/projects/{id}/buckets`
- `DELETE /api/v1/projects/{id}/buckets/{bucketId}`

### Endpoints Runtime Plane (`/storage/v1`)
- `GET /storage/v1/{bucket}` — listar objetos (query `prefix`).
- `POST /storage/v1/{bucket}/{*path}` — upload (raw body).
- `GET /storage/v1/{bucket}/{*path}` — download (o con `?expires=&signature=`).
- `DELETE /storage/v1/{bucket}/{*path}` — delete.
- `POST /storage/v1/{bucket}/signed/{*path}?expiresIn=…` — URL firmada (requiere Bearer).

### URLs firmadas
- Firmadas con HMAC-SHA256 usando un `STORAGE_SIGNING_KEY` del servidor.
- Payload: `{ bucket, path, expiresAt }`.
- Endpoint `GET /storage/v1/{bucket}/{*path}?signature=...&expires=...`.

### SDK
- `db.storage.from(bucket).upload(path, file)`
- `db.storage.from(bucket).download(path)`
- `db.storage.from(bucket).remove([path1, path2])`
- `db.storage.from(bucket).list({ prefix })`
- `db.storage.from(bucket).createSignedUrl(path, expiresInSeconds)`

## Subfases
1. Backend: entidades, migración, repositorio, servicio, endpoints Control y Runtime, tests.
2. SDK: cliente storage con upload/download/list/remove/signed URL.
3. Astro: página de demo `/almacen`.
4. Documentación y validación final.
