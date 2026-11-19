# Fase 5 — SDK TypeScript `@domibase/client` — completada

## Objetivo
Entregar un SDK TypeScript propio, sin dependencias de Supabase, que consuma la Runtime API existente (`/rest/v1/{table}`) desde navegador, Node.js, SSR y Astro, con transporte HTTP separado del query builder, errores normalizados y semántica de filas única probada.

## Entregado

### Paquete
- `packages/client` con nombre provisional `@domibase/client`, `private: true` (no se publica en npm todavía).
- ESM (`"type": "module"`), TypeScript estricto (`strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `verbatimModuleSyntax`) y declaraciones `.d.ts` con source maps.
- Sin dependencias de ejecución; `typescript` es la única dependencia de desarrollo.
- Scripts:
  - `npm run build` → `tsc -p tsconfig.json`.
  - `npm test` → build + `node --test "test/*.test.js"`.
  - `npm run clean`.
- El SDK no accede a `window` al importarse y usa `globalThis.fetch` solo al ejecutar una petición.

### API pública
```ts
import { createClient, DomiBaseError } from "@domibase/client";

const db = createClient({
  url: "http://localhost:5080",
  projectKey: "db_public_xxx",
  options: {
    timeout: 10_000,
    headers: { "x-app": "mi-app" },
    fetch: customFetch
  }
});

const { data, error, count } = await db
  .from("contactos")
  .select("id,nombre,email")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(20);
```

Métodos implementados:
- `createClient(config)` y `db.from<TRow>(table)`.
- `select(columns?)`, `insert(row | rows)`, `update(values)`, `delete()`.
- `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`.
- `order(column, { ascending? })`, `limit(n)`, `range(from, to)`.
- `abortSignal(signal)` para cancelación por consulta.
- `single()` y `maybeSingle()` con semántica probada.

### Contrato HTTP emitido
- Cabecera `apikey` con la clave pública del proyecto; el cliente nunca envía `projectId`, rol ni SQL.
- `select=col1,col2`, filtros repetidos `f=columna.operador.valor`, `order=columna.asc|desc`, `limit` y `offset`.
- `POST`/`PATCH` envían JSON con `Content-Type: application/json`; `DELETE` no envía cuerpo.
- `PATCH` y `DELETE` exigen al menos un filtro localmente y el Runtime lo revalida.
- `apikey` se aplica después de las cabeceras personalizadas: una cabecera del usuario no puede suplantar la clave del proyecto.

### Query builder
- Inmutable: cada método devuelve un builder nuevo; un `db.from("tabla")` base puede reutilizarse.
- Los límites locales replican el Runtime: 50 columnas de `select`, 10 filtros, 100 valores en `in`, 3 columnas de `order`, `limit` 1–1000, ventana de `range` ≤1000, `offset` ≤1 000 000 y 100 filas por `insert`.
- Valores serializados de forma determinista: números finitos, booleanos, `Date` en ISO 8601 y texto tal cual.
- `is` solo acepta `null`, `true` o `false`.
- Los valores de `in` no pueden contener comas porque el wire format usa la coma como separador.
- Identificadores de tabla y columna se validan localmente (longitud, caracteres de control, puntos/comas y barras) antes de construir la URL.

### Validación local y errores
Errores de argumentos inválidos lanzan `DomiBaseError` con `code: "INVALID_ARGUMENT"` (por ejemplo, `limit(0)`, `select("id,id")`, `insert([])`, tabla vacía). No se realiza ninguna petición de red.

Consultas incompletas resuelven el envelope sin red:
- Sin operación → `INVALID_QUERY`.
- `update`/`delete` sin filtros → `FILTER_REQUIRED`.

El transporte normaliza además:
- `INVALID_CONFIG`, `FETCH_UNAVAILABLE`, `NETWORK_ERROR`, `TIMEOUT`, `ABORTED`, `HTTP_ERROR`, `INVALID_RESPONSE` y los códigos del envelope del servidor.
- `DomiBaseError` conserva `code`, `message`, `details`, `hint` y `status`; `toJSON()` devuelve exactamente ese shape.
- Los errores 5xx no exponen SQL ni detalles internos: el SDK solo reenvía el mensaje recibido.

### Filas únicas
- `single()`: 1 fila → objeto; 0 o varias → `data: null` con `SINGLE_ROW_ERROR`.
- `maybeSingle()`: 0 filas → `data: null`, `error: null`; 1 fila → objeto; varias → `SINGLE_ROW_ERROR`.
- Un error del servidor se propaga tal cual; no se convierte en error de cardinalidad.
- La validación local (`INVALID_QUERY`, `FILTER_REQUIRED`) se mantiene antes de la semántica de fila única.

### Timeout y cancelación
- `options.timeout` en milisegundos (entero, 1–600 000). El temporizador se limpia siempre.
- `abortSignal(signal)` cancela una consulta concreta; una señal ya abortada no llama a `fetch`.
- Una cancelación externa produce `ABORTED`; el vencimiento del timeout produce `TIMEOUT`.
- El `fetch` inyectado recibe la señal combinada (timeout + cancelación externa).

## Verificación ejecutada

```powershell
cd packages/client
cmd /c npm install
cmd /c npm test
```

Resultado:
- Build TypeScript limpio (0 errores) con las opciones estrictas.
- **50 tests** en verde con el runner nativo de Node.js (`node --test`), repartidos en:
  - `test/query-builder.test.js` — URL/wire format, todos los operadores, orden, límites, mutaciones, validación local, inmutabilidad y cabeceras.
  - `test/single.test.js` — semántica de `single`/`maybeSingle` y propagación de errores.
  - `test/transport.test.js` — envelopes, errores HTTP/red/timeout/abort, normalización de URL y validación de configuración.
- No se usan paquetes ni código de Supabase.
- No se publicó el paquete ni se construyó imagen Docker.

## Limitaciones actuales
- El paquete sigue siendo privado y no publicado; el nombre `@domibase/client` debe verificarse en npm antes de publicar.
- No implementa Auth, Realtime ni Storage (fuera del alcance de Fase 1).
- No incluye reintentos automáticos ni deduplicación de peticiones.
- Los tests del SDK usan un `fetch` inyectado; la integración con el Runtime real y Astro corresponde a la Fase 6.

## Siguiente
Fase 6: ejemplo `examples/astro-example` consumiendo el SDK contra la API local, creación de `paneles-solares`, tabla `contactos` con RLS y demostración de aislamiento entre proyectos y de una clave inválida.
