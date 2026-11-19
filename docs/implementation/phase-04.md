# Fase 4 — Runtime Plane — completada

## Objetivo
Convertir `/rest/v1/{table}` en una API de datos real: resolver el proyecto server-side a partir de una API key pública, conectar con un login PostgreSQL propio del proyecto, ejecutar CRUD validado bajo RLS y devolver errores normalizados sin secretos ni SQL. El Control Plane no cambia de contrato.

## Entregado

### Credenciales de Runtime por proyecto
- Migración EF Core `20261117014359_AddRuntimeCredentials`: columna `projects.runtime_password_ciphertext`.
- El provisioning crea el login técnico `<db>_runtime` como **`LOGIN NOINHERIT`** (sin superuser, `CREATEDB`, `CREATEROLE`, `REPLICATION` ni `BYPASSRLS`) con una contraseña aleatoria de 32 bytes en base64url, y le concede membresía en `<db>_anon` para poder hacer `SET ROLE`.
- `<db>_owner` y `<db>_anon` siguen siendo `NOLOGIN`.
- La contraseña **nunca** se guarda en claro: se cifra con **AES-256-GCM** (`ISecretProtector` / `AesGcmSecretProtector`) usando `DOMIBASE_SECRETS_KEY` (base64 que debe decodificar exactamente 32 bytes). El cifrado incluye nonce y tag autenticado; una clave distinta o un ciphertext dañado hacen fallar la desprotección.
- `ProjectService` genera la contraseña una sola vez, la reutiliza en reintentos y la regenera si el ciphertext no se puede descifrar. Un proyecto `Ready` sin credenciales usables se re-provisiona al reintentar.
- `.env.example` documenta `DOMIBASE_SECRETS_KEY` y los parámetros `RUNTIME_*`.

### Resolución de proyecto por API key
- Solo se aceptan claves con prefijo `db_public_` y longitud acotada; las claves `db_secret_`, revocadas, inexistentes o de otro tipo devuelven **401 `INVALID_API_KEY`**.
- La comparación usa únicamente el hash HMAC-SHA-256 almacenado (nunca el valor, nunca comparación de texto plano).
- Proyecto no `Ready` → **403 `PROJECT_NOT_READY`**; credenciales de Runtime inservibles → **503 `PROJECT_UNAVAILABLE`**.
- `last_used_at` se actualiza como máximo una vez por minuto y un fallo de telemetría nunca rompe la petición.
- El contexto resuelto contiene `ProjectId`, `ApiKeyId`, base, login técnico, contraseña y rol funcional `_anon`; el cliente no envía ninguno de estos valores.

### Conexión y transacción de Runtime
- `RuntimeConnectionFactory` abre conexiones con el login del proyecto y **sin credenciales administrativas**; pooling independiente por base/credenciales.
- Cada petición usa una **transacción corta `ReadCommitted`**:
  1. `set_config('role', '<slug>_anon', true)` y `search_path = public`.
  2. `set_config('request.jwt.claims', '{"role":"anon","project_id":"...","api_key_id":"..."}', true)`.
  3. `set_config('domibase.project_id', ..., true)` y `set_config('domibase.api_key_id', ..., true)`.
  4. Metadatos de tabla/columnas, operación y `COMMIT` (o rollback ante error).
- Todos los valores de `set_config` son locales a la transacción; al cerrar, el rol y las claims desaparecen. Un test verifica que una conexión reutilizada del pool no conserva ni el rol ni el claim.

### Exposición de tablas y allowlists
- Solo se exponen tablas del esquema `public` con `relkind IN ('r','p')` y **RLS habilitado**. Tablas internas, tablas sin `GRANT`, tablas con RLS deshabilitado y nombres inventados devuelven **404 `TABLE_NOT_FOUND`**.
- Los privilegios por tabla y **por columna** se leen del catálogo (`has_table_privilege` / `has_column_privilege`) bajo el rol funcional: el builder nunca emite una columna que PostgreSQL vaya a rechazar.
- `select`/filtros/`order` usan solo columnas con `SELECT`; `INSERT` exige privilegio de inserción por columna; `PATCH` exige privilegio de actualización por columna; `DELETE` exige privilegio de tabla. Columnas no disponibles responden igual que una columna inexistente (`INVALID_COLUMN`) para no revelar el esquema.
- Los identificadores se validan contra el catálogo y se citan; los tipos admitidos (texto, enteros, numéricos, boolean, uuid, fecha/hora, interval, json/jsonb, inet/cidr, etc.) determinan el cast del parámetro.
- `RETURNING` solo incluye columnas legibles; si no hay privilegio de lectura no se devuelve ninguna fila.

### Endpoints
| Método | Ruta | Cabecera | Resultado |
|---|---|---|---|
| GET | `/rest/v1/{table}` | `apikey` | `200 { data, count }` |
| POST | `/rest/v1/{table}` | `apikey` | `201 { data, count }`; acepta objeto o array |
| PATCH | `/rest/v1/{table}` | `apikey` | `200 { data, count }`; exige al menos un `f` |
| DELETE | `/rest/v1/{table}` | `apikey` | `200 { data, count }`; exige al menos un `f` |

- `count` es el número de filas devueltas/afectadas de la operación.
- El proyecto se resuelve siempre server-side; no existen rutas de administración bajo `/rest/v1`.
- La clave administrativa `X-DomiBase-Admin-Token` no tiene efecto en Runtime (la petición se trata como operación pública sin `apikey`).

### Validación de consulta y cuerpo
- `select` (máx. 50 columnas), `f` (máx. 10 filtros, `in` máx. 100 valores), `order` (máx. 3 columnas, `asc`/`desc`), `limit` (1–1000, por defecto 100) y `offset` (0–1 000 000).
- Operadores: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`.
- `is` solo acepta `null`, `true` y `false`; los dos últimos requieren columna boolean.
- Cuerpos: un objeto o un array de objetos para `POST`; solo objeto para `PATCH`; máximo 100 filas por `POST`; JSON inválido, duplicado de campos o tipo no soportado devuelve error de validación.

### Límites y rate limiting
- Rate limiting con ventana fija (ASP.NET Core RateLimiter): en Fase 4 era **por dirección IP remota** (`RUNTIME_RATE_LIMIT_PERMITS` 300 cada `RUNTIME_RATE_LIMIT_WINDOW_SECONDS` 60); **P1.4 lo sustituye por cuota por proyecto** con override configurable y fallback por IP para claves inválidas. Rechazo canónico **429 `RATE_LIMITED`**. Se evalúa antes del límite de payload. Detalle vigente en `docs/implementation/p1-4-rate-limits.md`.
- Body máximo `RUNTIME_MAX_BODY_BYTES` (1 MiB; 1 KiB–16 MiB); por encima → **413 `PAYLOAD_TOO_LARGE`**. Se comprueba `Content-Length` y se limita el feature de tamaño del servidor.
- Timeouts configurables de conexión (10 s) y comando (15 s).

### Errores normalizados
| HTTP | Código | Origen |
|---|---|---|
| 401 | `MISSING_API_KEY` | Sin `apikey` |
| 401 | `INVALID_API_KEY` | Clave inexistente, revocada, `secret` o mal formada |
| 403 | `PROJECT_NOT_READY` | Proyecto en provisioning/failed |
| 403 | `FORBIDDEN` | GRANT/RLS insuficiente (`42501`) |
| 404 | `TABLE_NOT_FOUND` | Tabla no expuesta/inexistente |
| 409 | `CONFLICT` / `FOREIGN_KEY_VIOLATION` | Unicidad o clave foránea |
| 413 | `PAYLOAD_TOO_LARGE` | Body/rows por encima del límite |
| 422 | `VALIDATION_ERROR` | `NOT NULL`, `CHECK` u otra restricción |
| 429 | `RATE_LIMITED` | Ventana de rate limit agotada |
| 503 | `DATABASE_UNAVAILABLE` / `PROJECT_UNAVAILABLE` | Conexión/base no disponible o credenciales inválidas |
| 504 | `QUERY_TIMEOUT` | Consulta cancelada |
| 400 | `INVALID_FILTER*`, `INVALID_COLUMN`, `INVALID_LIMIT/ORDER/VALUE`, `INVALID_BODY`, `FILTER_REQUIRED`, `UNSUPPORTED_COLUMN_TYPE` | Validación de la petición |

Los mensajes 5xx son genéricos: no incluyen SQL, nombres de columnas ocultas, stack traces ni credenciales. El detalle PostgreSQL se registra solo en logs del servidor.

## Verificación ejecutada

### Build
```
dotnet build backend/DomiBase.sln
Build succeeded. 0 Warning(s), 0 Error(s)
```

### Tests
```
dotnet test backend/DomiBase.sln
DomiBase.UnitTests          73 passed
DomiBase.IntegrationTests   19 passed
DomiBase.SecurityTests       6 passed
Total                       98 passed, 0 failed
```

- **Unitarios nuevos (24):** SQL generado para SELECT/INSERT/UPDATE/DELETE, parámetros y casts por tipo, filtros/operadores, `is`, `in`, orden, límites, columnas no legibles, privilegios por operación, `FILTER_REQUIRED`, tipos no soportados, conversión JSON (escalares, nulos, arrays, duplicados, cuerpos inválidos).
- **Integración nuevos (12, PostgreSQL real):** ciclo CRUD completo; inserción masiva; todos los operadores; orden/paginación; filtros obligatorios; validaciones y errores; 404 de tabla no expuesta; RLS sin política (SELECT vacío, INSERT 403); aislamiento por claims de proyecto; aislamiento entre dos proyectos reales; inyección SQL en valores/identificadores; límite de payload; aislamiento de rol/claims en conexiones pooled.
- **Seguridad (2 actualizados):** `/rest/v1` sin token administrativo responde 401 `MISSING_API_KEY` (nunca 501 ni acceso administrativo), y el token administrativo no abre rutas de Control a través de Runtime.
- La asamblea de integración sigue ejecutándose en serie; crea y elimina bases/roles `it-*` reales y requiere PostgreSQL, `.env`, pepper y clave de secretos.
- Estado de PostgreSQL tras las pruebas: `projects=0`, `api_keys=0`, `provisioning_operations=0`, `audit_entries=0`; sin bases/roles `it-*`. Se conserva intencionalmente `domibase_smoke_check`.

## Limitaciones conocidas
- **Limitación histórica resuelta en P1.4:** el rate limit ya es por proyecto (en memoria del único host) con fallback por IP; sigue sin haber almacén distribuido, así que varias réplicas necesitarían uno. Detrás de un proxy debe configurarse `ForwardedHeaders` para ver la IP real del fallback.
- Solo se exponen tablas base del esquema `public`; vistas, vistas materializadas y otros esquemas no están soportados.
- Una tabla con `GRANT` pero sin RLS **no se expone** (404), por diseño. Crear tablas, `GRANT` y políticas sigue siendo una operación administrativa; todavía no hay endpoint de DDL en el Control Plane.
- El `POST` masivo ejecuta una sentencia por fila dentro de la misma transacción; no es un único `INSERT ... VALUES (...), (...)`. Es sencillo y seguro, pero menos eficiente para lotes grandes.
- `count` es el número de filas de la respuesta, no un `COUNT(*)` exacto independiente de la paginación.
- Sin endpoint de borrado de proyecto ni rotación dedicada de claves/contraseñas de Runtime.
- La rotación de `DOMIBASE_SECRETS_KEY` invalidaría todos los ciphertexts existentes; no hay re-cifrado implementado.
- En desarrollo `RUNTIME_DB_SSL_MODE=Prefer`; en despliegue debe endurecerse (`Require`/`VerifyFull`) al configurar TLS en PostgreSQL.
- El login técnico de Runtime se crea por proyecto y su contraseña se regenera solo si el ciphertext no se puede descifrar; no se rota periódicamente.

## Siguiente fase
**Fase 5 — SDK TypeScript `@domibase/client`:** transporte HTTP propio, query builder inmutable, `select/insert/update/delete`, filtros/orden/límites/range, `single`/`maybeSingle`, errores tipados, `fetch` inyectable/timeout/`AbortSignal`, SSR seguro y pruebas contra el Runtime real.
