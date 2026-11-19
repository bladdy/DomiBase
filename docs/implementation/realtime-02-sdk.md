# Realtime — subfase 3.2: SDK realtime

Segunda subfase de la Fase 3 de producto.

## Entregado

### Dependencia
- `@microsoft/signalr` `^8.0.7` añadida a `@domibase/client`.

### API
```ts
import { createClient } from "@domibase/client";

const db = createClient({ url: "http://localhost:5080", projectKey: "db_public_xxx" });

const unsubscribe = db.realtime.channel("contactos").subscribe((event) => {
  // event.table, event.operation, event.rowId, event.timestamp
});

await db.realtime.channel("contactos").unsubscribe();
```

### `RealtimeClientImpl`
- Ubicación: `packages/client/src/realtime.ts`.
- Conexión bajo demanda; se inicia al crear el primer canal.
- Se detiene cuando no quedan canales activos.
- Envía `apikey` siempre y `Authorization: Bearer` si hay sesión activa.
- Transportes: WebSockets con fallback a SSE.
- Reutiliza un único `HubConnection` para todos los canales del cliente.
- Callbacks de usuario se ejecutan aisladamente; un callback que falle no afecta a los demás.

### Validaciones
- Rechaza nombres de tabla vacíos o mayores de 63 caracteres.
- Rechaza uso directo de `subscribe`/`unsubscribe` en `db.realtime`.

### Verificación

```powershell
cd packages/client
npm.cmd test
```

- **59 tests SDK en verde**.
