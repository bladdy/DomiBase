# P1.4 — Rate limit por proyecto

**Fecha:** 2026-11-18  
**Estado:** implementado, probado y documentado.  
**Alcance:** sustituir el limitador por IP del Runtime Plane por cuotas configurables por proyecto, editables solo desde la Control Plane, sin Redis ni almacén distribuido.

## Diagnóstico y decisión

Antes de P1.4 el limitador de ASP.NET Core particionaba por `RemoteIpAddress`. Eso castigaba a varias aplicaciones detrás de la misma IP y no permitía dar más cupo a un proyecto concreto. Se evaluó una cuota global por clave y una cuota por proyecto:

- **Por clave:** rotar o emitir una clave reiniciaba el cupo y obligaba a replicar la configuración en cada clave.
- **Por proyecto (elegido):** todas las claves públicas de un proyecto comparten una ventana; el cupo sigue al proyecto aunque se roten claves.

El único host ASP.NET Core permite mantener el limitador **en memoria**; no se introduce Redis. En una futura extracción a varias réplicas hará falta un almacén compartido, y esa decisión queda documentada aquí.

## Contrato

### Valores y overrides

| Parámetro | Default global | Rango | Override por proyecto |
| --- | --- | --- | --- |
| `RUNTIME_RATE_LIMIT_PERMITS` | 300 | 1–10000 | `projects.rate_limit_permits` (`NULL` hereda) |
| `RUNTIME_RATE_LIMIT_WINDOW_SECONDS` | 60 | 1–3600 | `projects.rate_limit_window_seconds` (`NULL` hereda) |

- Un proyecto con ambos campos a `NULL` usa los defaults. Un valor fuera de rango se rechaza con **400 `VALIDATION_ERROR`** antes de tocar la base.
- Las variables de entorno se validan al arrancar; un valor inválido produce `InvalidOperationException` con el nombre de la variable, nunca un silencioso fallback.
- La migración EF `20261118032531_AddProjectRateLimits` añadió dos columnas nullable a `domibase_control.projects`. Se generó con `dotnet ef migrations add` y se aplicó solo a la base de control local con `dotnet ef database update`. La API **no aplica migraciones** al arrancar.

### Partición

- Clave pública válida y proyecto `Ready` → `project:{projectId:D}:{permits}:{windowSeconds}`. Todas las claves del mismo proyecto comparten cupo. Incluir los límites efectivos en la clave hace que cambiar el override abra una ventana nueva en lugar de heredar contadores antiguos.
- Clave ausente, mal formada, revocada o proyecto no `Ready` → `ip:{RemoteIpAddress}` con los defaults globales. Un atacante con claves inválidas no puede agotar la cuota de un proyecto legítimo.
- La clave API **nunca** entra en la partición ni en logs; el mensaje de rechazo es el sobre canónico **429 `RATE_LIMITED`** sin identificadores ni SQL.
- Rotar, revocar o recrear claves no reinicia el cupo: la partición depende de `projectId`, no de `apiKeyId`.

### Superficie cubierta

La política `"runtime"` se aplica a:

- `/rest/v1` (CRUD)
- `/storage/v1` (objetos y URLs firmadas)
- `/functions/v1` (invocación WASM)
- `/auth/v1` (registro, login, refresh, logout)
- `/realtime/v1` (solo el handshake HTTP/SSE/WebSocket; **no** los frames después de conectar)

`RuntimeResolutionMiddleware` resuelve el `apikey` una sola vez por petición y guarda el resultado en `HttpContext.Items`. Los endpoints, incluido `RealtimeHub`, reutilizan esa decisión a través de `RuntimeRequestContext`; no hay una segunda consulta a la base de control por petición.

### Control Plane

```
PATCH /api/v1/projects/{id}/rate-limit
Content-Type: application/json
X-DomiBase-Admin-Token: <token>

{ "permits": 2, "windowSeconds": 60 }
```

- `null` en ambos campos limpia el override y restaura los defaults.
- La respuesta `ProjectResponse` expone `rateLimitPermits`, `rateLimitWindowSeconds`, `effectiveRateLimitPermits` y `effectiveRateLimitWindowSeconds`.
- Cada cambio escribe la auditoría `project.rate_limit.updated` con slug y valores efectivos; nunca incluye claves ni secretos.
- Sin token administrativo, o con solo una clave pública `apikey`, la ruta devuelve 401. La configuración jamás existe bajo `/rest/v1`.

## Panel privado

La pestaña Proyectos incorpora una columna **Cupo** con el valor efectivo, la ventana y el origen (`Heredado`/`Personalizado`). El botón **Cupo** abre un `<dialog>` nativo con ambos campos; dejarlos vacíos restaura el default global. Se reutilizan `ControlClient`, el token en `sessionStorage`, las clases visuales existentes y el patrón de confirmación/diálogo del panel. La validación local replica los rangos del servidor; el servidor vuelve a validar.

## Verificación

```powershell
dotnet build backend/DomiBase.sln
dotnet test  backend/DomiBase.sln --no-build
# 383/383: 244 unitarios, 116 integración, 23 seguridad

cd frontend/admin
npm.cmd test     # 35/35
npm.cmd run build
cd ..
node scripts/dev/verify-rate-limits-panel.mjs   # 14/14
```

Cobertura nueva relevante:

- Unitarios: rangos y defaults, configuración inválida al arrancar, claves de partición sin material de clave.
- Integración (`ProjectRateLimitTests`): dos proyectos con cupos distintos contra PostgreSQL real; tercera petición 429; segunda clave del mismo proyecto comparte cupo; claves inválidas caen a IP; validación, aislamiento administrativo y auditoría del `PATCH`.
- E2E (`scripts/dev/verify-rate-limits-panel.mjs`): crea dos proyectos temporales, edita cupos desde el panel, comprueba la Control API y el comportamiento real de `/rest/v1`, verifica la persistencia tras recargar, restaura el default, revisa 390×844 sin desbordamiento y elimina ambos proyectos. No imprime token ni claves.

Capturas: `.impeccable/review/rate-limits-desktop.png` y `.impeccable/review/rate-limits-mobile.png`.

No se construyó ni publicó la imagen Docker de la API, no se hicieron commits/pushes y no se tocó el homelab.

### Modern Web Guidance

- **`responsive-table`**: mantuve la tabla semántica existente con `<thead>`, `<tbody>` y `<th scope="col">`, y el contenedor con desbordamiento para móvil, en vez de sustituirla por tarjetas o `div`s; verifiqué 390×844 sin desbordamiento horizontal ("Use standard `<table>` elements with `<thead>`, `<tbody>`, and `<th>` elements.").
- **`dialog`**: reutilicé el `<dialog>` nativo con `showModal()` para el editor de cupo, sin `role="dialog"`, `aria-modal` ni focus trap propio; el navegador aporta foco, capa superior y `Esc` ("Use the native `<dialog>` element for modal and non-modal dialogs. It provides the accessibility semantics, focus handling, top-layer rendering and `Esc` handling that custom overlays have to rebuild by hand.").
- **`accessibility`**: conservé los estilos de foco del panel basados en `:focus-visible` para el nuevo botón y los campos numéricos, sin añadir reglas `:focus` que marquen el foco del ratón ("When defining custom focus indicators, always style the `:focus-visible` state (not `:focus`) explicitly.").
