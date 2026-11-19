# Contrato REST del Runtime API

Base local de desarrollo: `http://localhost:5080`. El proyecto se resuelve **server-side** a partir de la API key pública (`apikey: db_public_...`). El cliente nunca envía `projectId`, `role`, base de datos ni SQL.

> **Estado Fase 4 (implementado):** verificación de la clave (solo hash HMAC), resolución del proyecto, conexión con el login `_runtime` del proyecto, `SET ROLE` transaccional al rol `_anon`, claims `request.jwt.claims`/`domibase.project_id`, allowlists desde el catálogo y CRUD bajo RLS. Ver `docs/implementation/phase-04.md`.

## Cabeceras
- `apikey: db_public_...` (obligatoria en Runtime).
- `Authorization: Bearer ...` reservado para fase de Auth (no implementado en Fase 1).
- `Content-Type: application/json` en cuerpos.
- `X-DomiBase-Admin-Token` **no** tiene efecto bajo `/rest/v1`.

## Operaciones
| Método | Ruta | Uso | Éxito |
|---|---|---|---|
| GET | `/rest/v1/{table}` | Selección con filtros/orden/límites | `200` |
| POST | `/rest/v1/{table}` | Inserción (objeto o array de objetos) | `201` |
| PATCH | `/rest/v1/{table}` | Actualización acotada por filtros | `200` |
| DELETE | `/rest/v1/{table}` | Eliminación acotada por filtros | `200` |

Reglas de exposición:
- Solo tablas del esquema `public` con RLS habilitado y algún privilegio para el rol funcional.
- Tablas sin `GRANT`, sin RLS, inexistentes o de otro esquema → `404 TABLE_NOT_FOUND`.
- Los privilegios por columna determinan `select`, `insert`, filtros y `order`; una columna no legible responde `400 INVALID_COLUMN` (mismo mensaje que una inexistente).

## Parámetros de consulta
- `select=col1,col2` — máximo 50 columnas; por defecto todas las legibles.
- `f=columna.operador.valor` — repetible; máximo 10 filtros.
  - Operadores permitidos: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`.
  - `in` admite hasta 100 valores separados por coma.
  - `is` solo acepta `null`, `true` y `false` (estos dos últimos solo en columnas boolean).
  - Ejemplos: `f=estado.eq.nuevo`, `f=id.in.1,2,3`, `f=deleted_at.is.null`.
- `order=columna.asc[,columna2.desc]` — máximo 3 columnas.
- `limit=N` — 1–1000; por defecto 100.
- `offset=N` — 0–1 000 000.

`PATCH` y `DELETE` requieren al menos un filtro `f`; el servidor rechaza operaciones no acotadas con `400 FILTER_REQUIRED`.

### Cuerpos
- `POST`: un objeto o un array de objetos; máximo 100 filas por petición.
- `PATCH`: un objeto con los campos a actualizar.
- Los valores JSON se convierten a texto y se envían como parámetros; el servidor los castea al tipo de la columna.
- `null` se convierte en `NULL`.
- JSON inválido, array vacío, campos duplicados o item de array que no sea objeto → `400 INVALID_BODY`.

## Respuestas
Éxito:
```json
{ "data": [ ... ], "count": 2 }
```

`count` es el número de filas devueltas o afectadas por la operación (no un `COUNT(*)` independiente de la paginación).

Error:
```json
{ "error": { "code": "VALIDATION_ERROR", "message": "...", "details": "...", "hint": "..." } }
```

### Códigos de error del Runtime
| HTTP | Código | Significado |
|---|---|---|
| 400 | `INVALID_FILTER`, `INVALID_FILTER_OPERATOR`, `INVALID_FILTER_VALUE` | Filtro mal formado u operador/valor no permitido |
| 400 | `INVALID_COLUMN`, `INVALID_SELECT`, `INVALID_LIMIT`, `INVALID_OFFSET`, `INVALID_ORDER`, `INVALID_VALUE` | Parámetros o columnas no válidos |
| 400 | `INVALID_BODY`, `FILTER_REQUIRED`, `UNSUPPORTED_COLUMN_TYPE` | Cuerpo inválido, escritura sin filtro o tipo no soportado |
| 401 | `MISSING_API_KEY`, `INVALID_API_KEY` | Clave ausente, inválida, revocada o de tipo `db_secret_` |
| 403 | `PROJECT_NOT_READY`, `FORBIDDEN` | Proyecto no listo o GRANT/RLS insuficiente |
| 404 | `TABLE_NOT_FOUND` | Tabla no expuesta o inexistente |
| 409 | `CONFLICT`, `FOREIGN_KEY_VIOLATION` | Unicidad o referencia de clave foránea |
| 413 | `PAYLOAD_TOO_LARGE` | Body o número de filas por encima del límite |
| 422 | `VALIDATION_ERROR` | `NOT NULL`, `CHECK` u otra restricción |
| 429 | `RATE_LIMITED` | Rate limit agotado |
| 500 | `DATABASE_ERROR`, `INTERNAL_ERROR` | Error interno sin detalle sensible |
| 503 | `DATABASE_UNAVAILABLE`, `PROJECT_UNAVAILABLE` | Base o credenciales de Runtime no disponibles |
| 504 | `QUERY_TIMEOUT` | Consulta cancelada por timeout |

## Reglas de seguridad
- Valores siempre parametrizados; nunca se interpola texto del cliente en SQL.
- Identificadores validados contra el catálogo y quoted; no se aceptan fragmentos SQL.
- RLS activo obligatorio; Runtime no usa superuser, owner ni `BYPASSRLS`.
- Transacción corta con `SET LOCAL ROLE`/`set_config(..., true)`; el contexto no sobrevive ni se filtra entre conexiones pooled.
- Errores sin secretos, stack traces ni SQL interno.
- Rate limiting por proyecto desde P1.4 (todas las claves del proyecto comparten cupo; claves inválidas o ausentes se limitan por IP con los defaults globales) y límites de payload/filas. El override se configura solo en la Control Plane; detalle en `docs/implementation/p1-4-rate-limits.md`.
- Los backups y su restauración son operaciones exclusivas de la Control Plane (`/api/v1/projects/{id}/backups`, P1.6); ninguna clave pública ni ruta `/rest/v1` puede crear, descargar, borrar ni restaurar volcados.
- La traza administrativa y los pasos de provisioning solo se leen desde la Control Plane (`GET /api/v1/audit` y `GET /api/v1/projects/{id}/provisioning-operations`, P1.7). Ninguna clave pública ni ruta `/rest/v1` puede consultarlos; `details`/`Error` se redactan y los filtros se validan contra allowlists antes de consultar la base de control.

## Vista previa administrativa (Control Plane; no Runtime)

La Control Plane expone una lectura acotada para el panel, protegida por `X-DomiBase-Admin-Token`:

| Método | Ruta | Uso |
| --- | --- | --- |
| `GET` | `/api/v1/projects/{projectId}/data/tables/{table}/rows` | Ver filas exactamente como las ve el rol `anon`. |

Acepta `select`, `f` (repetible, `columna.operador.valor`), `order`, `limit` y `offset` con los mismos operadores y validaciones del Runtime. Los límites específicos de la vista previa son `limit` 1–200 (por defecto 50) y `offset` 0–100.000; hasta 50 columnas y 10 filtros. La respuesta es `{ role, columns, rows, rowCount, truncated }` y `role` siempre es `"anon"`.

No acepta `role`, base de datos, SQL ni clave de API. La petición resuelve el proyecto por ID desde el token administrativo, reutiliza `IRuntimeDataService`, aplica `SET LOCAL ROLE` al rol `_anon` y las claims del proyecto, y registra `data.preview` sin filtros literales. Esta ruta no existe bajo `/rest/v1`; las políticas RLS se administran mediante `/api/v1/projects/{id}/schema/tables/{table}/policies` (P1.1) con expresiones allowlisted. Ver `docs/implementation/p2-1-data-explorer-rls.md`.

## Sistema
- `GET /health` — liveness.
- `GET /health/ready` — readiness (incluye comprobación de dependencias críticas).
