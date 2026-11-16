# Contrato inicial del SDK DomiBase

Paquete provisional: `@domibase/client`. Verificar disponibilidad en npm antes de publicar.

## Inicialización
```ts
import { createClient } from "@domibase/client";

const db = createClient({
  url: "https://api.example.com",
  projectKey: "db_public_xxx",
});
```

## Configuración
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

## Database
```ts
const { data, error } = await db
  .from("contactos")
  .select("id,nombre,email")
  .eq("estado", "nuevo")
  .order("created_at", { ascending: false })
  .limit(20);
```

Operaciones iniciales: `select`, `insert`, `update`, `delete`.

Filtros: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`.

Paginación: `order`, `limit`, `range`.

## Respuesta
```ts
export interface DomiBaseResponse<T> {
  data: T | null;
  error: DomiBaseError | null;
  count?: number | null;
}

export interface DomiBaseError {
  code: string;
  message: string;
  details?: string;
  hint?: string;
  status?: number;
}
```

## Requisitos del SDK
- TypeScript con declaraciones `.d.ts`.
- Compatible con browser y Node/SSR.
- No acceder a `window` ni `localStorage` al importar.
- `fetch` inyectable.
- Soportar `AbortSignal` y timeout.
- No depender de paquetes Supabase.
- Errores HTTP normalizados.
- Query builder inmutable o con semántica claramente documentada.
- Validar que el query builder no pueda emitir operadores ni nombres de columna arbitrarios sin validación del servidor.

## Fuera de fase 1
Auth, Realtime, Storage y Functions. No exponer métodos falsos o mocks. Documentar como roadmap.
