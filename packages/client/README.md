# @domibase/client

SDK TypeScript propio de DomiBase para la API pública de datos (`/rest/v1`). Permite consultar y escribir filas usando la API key pública de un proyecto.

> **Versión actual:** `0.1.0` (tag `latest`).  
> **API incluida:** Fase 1 — CRUD de datos.  
> **No incluido en esta versión:** `auth`, `realtime`, `storage` ni `functions`.

El SDK es solo un cliente HTTP: **no incluye servidor, PostgreSQL ni panel de administración**. Necesitas un backend DomiBase accesible y un proyecto provisionado.

## Requisitos

- Node.js `>= 20`, o navegador/bundler con `fetch` disponible.
- ESM: usa `import`. `require("@domibase/client")` no está soportado.
- Backend DomiBase en ejecución y proyecto en estado `Ready`.
- Clave pública con formato `db_public_...`.
- Tabla expuesta y permisos/RLS configurados en el servidor.

## Instalación

```bash
npm i @domibase/client
```

Versión publicada: `0.1.0` (tag `latest`). Para fijar la versión exacta:

```bash
npm i @domibase/client@0.1.0
```

Esta versión incluye únicamente la API Fase 1; no incluye `auth`, `realtime`, `storage` ni `functions`.

## Inicio rápido

```ts
import { createClient } from "@domibase/client";

const db = createClient({
  url: "https://api.tudominio.com",
  projectKey: "db_public_xxx"
});

const { data, error, count } = await db
  .from("contactos")
  .select("id,nombre,email")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(20);

if (error) {
  console.error(error.code, error.message, error.status);
} else {
  console.log(data, count);
}
```

`url` debe ser absoluta `http(s)`, sin credenciales, query ni hash; se normaliza sin barra final. `projectKey` debe empezar por `db_public_`. Una configuración inválida lanza `DomiBaseError` con `code: "INVALID_CONFIG"`.

> `http://localhost:5080` solo funciona si el backend corre en la **misma máquina** que ejecuta este código. En producción usa la URL pública de tu API. Desde navegador, el servidor debe permitir el origen mediante CORS.

## Configuración

```ts
interface DomiBaseConfig {
  url: string;
  projectKey: string;
  options?: {
    timeout?: number;
    headers?: Record<string, string>;
    fetch?: typeof fetch;
  };
}
```

| Opción | Descripción |
| --- | --- |
| `timeout` | Milisegundos por petición, entre `1` y `600000`. |
| `headers` | Headers adicionales. `apikey` se deriva siempre de `projectKey` y no puede sobrescribirse. |
| `fetch` | Implementación inyectable para SSR, Node, tests o proxies. Si no hay `fetch` global, se usa `options.fetch`. |

El import no accede a `window`, así que es seguro en SSR/Node.

## Consultas

```ts
const { data, error } = await db
  .from("contactos")
  .select("id,nombre")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(10);
```

- `from(table)` selecciona la tabla.
- `select(columnas?)`: columnas separadas por coma; sin argumento devuelve todas.
- El query builder es **inmutable**: cada llamada devuelve un builder nuevo, por lo que puedes reutilizar uno base.

### Escritura

```ts
await db.from("contactos").insert({ nombre: "Ana", estado: "nuevo" });

await db.from("contactos").insert([
  { nombre: "Ana", estado: "nuevo" },
  { nombre: "Luis", estado: "nuevo" }
]);

await db
  .from("contactos")
  .update({ estado: "contactado" })
  .eq("id", 1);

await db
  .from("contactos")
  .delete()
  .eq("id", 1);
```

`insert` acepta de 1 a 100 filas. `update` y `delete` **requieren al menos un filtro**; si no, devuelven `FILTER_REQUIRED` sin hacer red.

Puedes pedir columnas en la respuesta de escrituras:

```ts
const { data } = await db
  .from("contactos")
  .insert({ nombre: "Ana" })
  .select("id,nombre");
```

### Filtros

```ts
db.from("contactos").eq("estado", "nuevo");
db.from("contactos").neq("estado", "archivado");
db.from("contactos").gt("precio", 100);
db.from("contactos").gte("precio", 100);
db.from("contactos").lt("precio", 500);
db.from("contactos").lte("precio", 500);
db.from("contactos").like("nombre", "Ana%");
db.from("contactos").ilike("nombre", "%ana%");
db.from("contactos").in("estado", ["nuevo", "contactado"]);
db.from("contactos").is("deleted_at", null);
db.from("contactos").is("activo", true);
```

### Orden y paginación

```ts
db.from("contactos").order("created_at", { ascending: false });

db.from("contactos").limit(20);

db.from("contactos").range(0, 19); // offset 0, 20 filas
```

### Fila única

```ts
const uno = await db
  .from("contactos")
  .select("id,nombre")
  .eq("id", 1)
  .single();

const quizasUno = await db
  .from("contactos")
  .select("id,nombre")
  .eq("email", "ana@example.com")
  .maybeSingle();
```

- `single()` devuelve error `SINGLE_ROW_ERROR` si hay 0 o más de 1 fila.
- `maybeSingle()` devuelve `{ data: null, error: null }` si hay 0 filas; también falla si hay más de 1.

### Timeout y abort

```ts
const controller = new AbortController();

const { data, error } = await db
  .from("contactos")
  .select("id")
  .abortSignal(controller.signal);

controller.abort();
```

También puedes fijar timeout global:

```ts
const db = createClient({
  url: "https://api.tudominio.com",
  projectKey: "db_public_xxx",
  options: { timeout: 5000 }
});
```

## Resultado y errores

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

Códigos propios del SDK: `TIMEOUT`, `ABORTED`, `NETWORK_ERROR`, `HTTP_ERROR`, `INVALID_RESPONSE`, `INVALID_CONFIG`, `FETCH_UNAVAILABLE`, `INVALID_QUERY`, `INVALID_ARGUMENT`, `FILTER_REQUIRED`, `SINGLE_ROW_ERROR`. El servidor puede devolver otros códigos, por ejemplo `FORBIDDEN`.

## Validación local

Antes de hacer red, el SDK valida:

| Límite | Valor |
| --- | --- |
| Columnas en `select` | 50 |
| Filtros | 10 |
| Valores en `in` | 100 |
| Columnas en `order` | 3 |
| `limit` | 1–1000 |
| `range` | máximo 1000 filas, offset hasta 1 000 000 |
| Filas en `insert` | 100 |

Los identificadores se validan y codifican de forma segura. El SDK **no acepta SQL arbitrario** ni permite enviar `projectId`, rol SQL ni el nombre de la base: todo se resuelve en servidor.

## Seguridad

- Usa solo claves `db_public_...` en cliente/navegador.
- La clave pública identifica el proyecto; **no garantiza acceso**. El acceso real depende de GRANT y RLS en servidor.
- Nunca envíes `DOMIBASE_ADMIN_TOKEN`, claves `db_secret_...`, contraseñas ni headers administrativos desde el SDK.
- CORS no es seguridad: configura el backend para permitir solo los orígenes que correspondan.

## Resolución de problemas

| Síntoma | Causa probable |
| --- | --- |
| `INVALID_CONFIG` | `url` no absoluta o `projectKey` no empieza por `db_public_`. |
| `FETCH_UNAVAILABLE` | Entorno sin `fetch` global; inyecta `options.fetch`. |
| `TIMEOUT` | Servidor lento o `timeout` demasiado bajo. |
| `ABORTED` | `AbortSignal` cancelado o señal ya abortada. |
| `HTTP_ERROR` / `FORBIDDEN` | Clave inválida/revocada, tabla no expuesta o RLS denegado. |
| Petición desde navegador bloqueada | Falta CORS en el backend. |
| `localhost` no responde en producción | La URL apunta a la máquina del consumidor, no al servidor. |
| `require()` falla | El paquete es ESM; usa `import`. |
| `npm i @domibase/client` instala una versión inesperada | Revisa `npm view @domibase/client dist-tags`; fija `@0.1.0`. |

## Desarrollo del paquete

```bash
npm test          # compila TypeScript y ejecuta node --test
npm run build     # solo compila
npm pack --dry-run
```

En PowerShell, si la política bloquea `npm.ps1`, usa `cmd /c npm ...`.

## Licencia

MIT.
