# Fase 3 de producto — Realtime — plan y decisiones

## Objetivo
Eventos en tiempo real por proyecto/tabla: cuando una fila cambia en PostgreSQL, los clientes suscritos reciben una notificación **después del commit**, con autorización de canal basada en API key y, opcionalmente, en usuario autenticado.

## Decisiones de diseño

### Transporte
- **SignalR** en el endpoint `/realtime/v1` (WebSockets con fallback SSE/LongPolling).
- Hub `RealtimeHub` con métodos `Subscribe(table)` y `Unsubscribe(table)`.
- Eventos enviados al grupo `project:{projectId}:table:{table}`.

### Autorización de conexión
- La conexión requiere la cabecera/query `apikey` pública, validada con `IRuntimeProjectResolver` en `OnConnectedAsync`.
- Si se envía `Authorization: Bearer`, se valida el JWT con la clave pública del proyecto y se almacenan los claims.
- Conexiones sin `apikey` válida o proyecto no `Ready` se rechazan.

### Autorización de canales y eventos
- `Subscribe(table)` solo permite tablas de `public` con RLS habilitado y con al menos un privilegio sobre la tabla (validado contra el catálogo con el rol funcional correspondiente).
- El payload de evento es **mínimo**: `{ table, operation, id, timestamp }`. El cliente decide si reconsulta vía Runtime, donde RLS aplica de forma autoritativa.
- No se filtran eventos por fila en el servidor; se notifica que "algo cambió" y el SDK puede refetch.

### Entrega después de commit
- `RuntimeDataService` invoca `IRealtimeNotifier.NotifyAsync(...)` **después de** `transaction.CommitAsync()`.
- Implementación por defecto: `SignalRRealtimeNotifier` publica en el grupo SignalR correspondiente.
- La interfaz permite en el futuro reemplazarla por un broker PostgreSQL LISTEN/NOTIFY sin tocar el Runtime.

### Contrato de evento
```json
{
  "table": "contactos",
  "operation": "INSERT",
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-11-17T20:00:00.000Z"
}
```

### SDK
- Dependencia `@microsoft/signalr`.
- `db.realtime.channel(table)` devuelve un objeto con `subscribe(callback)` / `unsubscribe()`.
- La conexión se inicia bajo demanda; se reusa entre canales del mismo cliente.
- Se envía `apikey` y, si existe sesión, `Authorization: Bearer` durante la negociación.

### Astro
- Página `/tiempo-real` que muestra un log de eventos recibidos para `contactos` y `notas`.

## Subfases
1. Backend SignalR: hub, notificador, integración con RuntimeDataService, tests de integración.
2. SDK realtime: cliente SignalR, tests unitarios.
3. Astro: página de demo y actualización del script de demo si es necesario.
4. Documentación y validación final.

## Restricciones mantenidas
- Sin Redis ni Kafka; un solo host ASP.NET Core.
- Sin exponer SQL ni secretos en eventos.
- PostgreSQL sigue siendo el único componente en Docker.
