# Realtime — subfase 3.1: backend SignalR

Primera subfase de la Fase 3 de producto. Detalle de decisiones en `realtime-plan.md`.

## Entregado

### Contrato `IRealtimeNotifier`
- Ubicación: `DomiBase.Application/Realtime/IRealtimeNotifier.cs`.
- Payload: `RealtimeEvent(Table, Operation, RowId, Timestamp)`.
- Interfaz desacoplada para permitir futuros brokers (p. ej. PostgreSQL LISTEN/NOTIFY) sin tocar el Runtime.

### `SignalRRealtimeNotifier`
- Ubicación: `DomiBase.Infrastructure/Realtime/SignalRRealtimeNotifier.cs`.
- Publica en el grupo `project:{projectId}:table:{table}`.
- Swallow de excepciones: la notificación es best-effort después del commit.

### `RealtimeHub`
- Ubicación: `DomiBase.Infrastructure/Realtime/RealtimeHub.cs`.
- Endpoint: `/realtime/v1`.
- `OnConnectedAsync`:
  - Lee `apikey` de cabecera o query string.
  - Resuelve el proyecto con `IRuntimeProjectResolver`.
  - Valida Bearer opcional con la clave pública activa del proyecto.
  - Almacena `RuntimeProjectContext` y `userId` en `Context.Items`.
- `Subscribe(table)`:
  - Abre una conexión al proyecto con `RuntimeConnectionFactory`.
  - Aplica `SET ROLE` al rol funcional (anon/authenticated).
  - Verifica que la tabla exista en `public`, tenga RLS habilitado y que el rol tenga al menos `SELECT`.
  - Añade la conexión al grupo SignalR correspondiente.
- `Unsubscribe(table)`:
  - Elimina la conexión del grupo.

### Integración con `RuntimeDataService`
- Inyectado `IRealtimeNotifier`.
- Tras `transaction.CommitAsync()`, se invoca `NotifyAsync` para operaciones `INSERT`, `UPDATE` y `DELETE`.
- `SELECT` no notifica.
- El `RowId` se extrae del primer campo `id` devuelto; si no hay, es `null`.
- Errores de notificación se loguean y no fallan la respuesta HTTP.

### Registro DI
- `AddSignalR()` y `IRealtimeNotifier` registrados en `AddDomiBaseRuntime`.
- Hub mapeado en `Program.cs`:
  ```csharp
  app.MapHub<DomiBase.Infrastructure.Realtime.RealtimeHub>("/realtime/v1");
  ```

## Verificación

```powershell
dotnet test backend/DomiBase.sln
```

- **141 tests backend en verde** (97 unitarios + 38 integración + 6 seguridad).
- Nuevos tests en `RealtimeTests.cs`:
  - Suscripción e inserción reciben evento.
  - `apikey` ausente rechaza la conexión/suscripción.
  - Tabla no expuesta rechaza la suscripción.
