# Contrato del SDK DomiBase

Paquete provisional: `@domibase/client`. Implementación propia en `packages/client` (ESM, TypeScript estricto). No se usan `@supabase/supabase-js`, GoTrue ni PostgREST.

## Instalación desde npm

```bash
npm i @domibase/client
```

Versión publicada: `0.1.0` (tag `latest`). Para fijar la versión exacta, usa `npm i @domibase/client@0.1.0`.

El paquete es ESM (`Node >= 20`): usar `import`, no `require`. Necesita un backend DomiBase accesible, una clave `db_public_...` de un proyecto `Ready` y RLS/GRANT configurados en servidor. Las APIs de Auth, Realtime, Storage y Functions **no están incluidas** en este paquete.

Para ejemplos completos de uso, instalación y resolución de problemas, ver el `README.md` del paquete.

> **Alcance soportado (Fase 1):** inicialización, query builder de datos y errores. Los módulos de Auth, Realtime, Storage y Functions existen como código diferido en el repositorio (`packages/client/deferred`), **no están incluidos en el paquete npm `0.1.0`**, no están en los exports públicos y no deben usarse en nuevas integraciones.

## Inicialización

```ts
import { createClient } from "@domibase/client";

const db = createClient({
  url: "http://127.0.0.1:5080",
  projectKey: "db_public_xxx"
});
```

`url` debe ser absoluta `http(s)`, sin credenciales; se normaliza sin barra final ni query/hash. `projectKey` debe empezar por `db_public_`. La configuración inválida lanza `DomiBaseError` con `code: "INVALID_CONFIG"`.

```ts
export interface DomiBaseConfig {
  url: string;
  projectKey: string;
  options?: {
    timeout?: number;
    headers?: Record<string, string>;
    fetch?: typeof fetch;
  };
}
```

`options.fetch` permite inyectar `fetch` (SSR/Node/browser); `options.timeout` acepta 1–600 000 ms. El import no accede a `window`.

## Base de datos

```ts
const { data, error, count } = await db
  .from("contactos")
  .select("id,nombre,email")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(20);
```

Escritura:

```ts
await db.from("contactos").insert({ nombre: "Juan", estado: "nuevo" });
await db.from("contactos").update({ estado: "contactado" }).eq("id", 1);
await db.from("contactos").delete().eq("id", 1);
```

- Métodos: `from`, `select`, `insert`, `update`, `delete`.
- Filtros: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`.
- Orden/paginación: `order`, `limit`, `range`.
- Resultados de fila única: `single`, `maybeSingle` (semántica probada: 0/1/varias filas).
- `insert` acepta un objeto o un array de hasta 100 filas y recibe `Partial<TRow>` para permitir defaults de PostgreSQL.
- `update`/`delete` sin filtros devuelven `FILTER_REQUIRED` sin red.
- El builder es inmutable.

### Validación local

Identificadores, valores, límites y señales se validan antes de red: 50 columnas, 10 filtros, 100 valores en `in`, 3 columnas de orden, `limit` 1–1000, `range` ≤1000 filas y `offset` ≤1 000 000. Una señal ya abortada produce `ABORTED` sin invocar `fetch`.

## Transporte HTTP

Solo se consume el Runtime API:

- `GET|POST|PATCH|DELETE /rest/v1/{table}`
- La clave pública viaja en `apikey`; el proyecto, la base, el rol SQL y los permisos se resuelven en el servidor.
- El SDK nunca envía `projectId`, `role` ni SQL.

Wire format validado también en servidor:

- `select=col1,col2` (máximo 50)
- `f=columna.operador.valor` (repetible, máximo 10)
- `order=columna.asc[,columna2.desc]` (máximo 3)
- `limit=N`, `offset=N`

Respuesta canónica:

```ts
interface DomiBaseResponse<T> {
  data: T | null;
  error: DomiBaseError | null;
  count?: number | null;
}

interface DomiBaseError {
  code: string;
  message: string;
  details?: string;
  hint?: string;
  status?: number;
}
```

Códigos de transporte: `TIMEOUT`, `ABORTED`, `NETWORK_ERROR`, `HTTP_ERROR`, `INVALID_RESPONSE`, `INVALID_CONFIG`, `FETCH_UNAVAILABLE`, `INVALID_QUERY`, `FILTER_REQUIRED`, `SINGLE_ROW_ERROR`.

> La API key pública identifica el proyecto; el acceso real depende de GRANT y RLS. Contrato HTTP completo en `docs/architecture/rest-contract.md`.

## Pruebas

```powershell
cd packages/client
cmd /c npm test
```

Compila TypeScript y ejecuta los tests con el runner nativo de Node.js. En PowerShell usar `cmd /c npm ...` si la política bloquea `npm.ps1`.
