# Fase 5 de producto — Functions — plan y decisiones

## Objetivo
Ejecutar código definido por el propietario por proyecto, con aislamiento real de procesos,
límites estrictos y sin ejecutar código arbitrario dentro del proceso del Runtime API.

## Decisión de arquitectura: worker one-shot + WASM sandbox

### Por qué no ejecutar en el Runtime API
El Runtime API es el único host ASP.NET Core y concentra las claves de provisioning, los hashes de
API key y las conexiones privilegiadas. Cargar un módulo de usuario en ese proceso rompería el
aislamiento incluso con un sandbox de lenguaje. Por tanto:

- El API **nunca** carga, compila ni interpreta el módulo del usuario.
- Cada invocación lanza un **proceso OS independiente** (`DomiBase.Functions.Worker`).
- Dentro del worker, el módulo WASM se ejecuta en un sandbox Wasmtime sin capacidades de I/O.

### Modelo de ejecución
- Las funciones son módulos **WebAssembly** (validados por Wasmtime antes de ejecutarse).
- ABI mínima y propia:
  - El módulo exporta `memory` y `invoke(i32 ptr, i32 len) -> i64`.
  - El host escribe el JSON de request en un offset fijo de la memoria lineal.
  - `invoke` devuelve `(offset << 32) | length` apuntando al JSON de respuesta dentro de la memoria.
- Request (JSON): `{ "input": <cualquier JSON>, "context": { "projectId", "userId"|null } }`.
- Respuesta: el JSON devuelto por el módulo se envuelve en `{ data }` en el endpoint Runtime.
- Sin WASI, sin directorios preabiertos, sin imports de sockets: el guest **no tiene** capacidad de
  red ni de filesystem. La red del worker ni siquiera se usa (comunicación por stdin/stdout).

### Límites
| Recurso | Mecanismo | Default | Máximo |
|---|---|---|---|
| CPU | Wasmtime **fuel** (presupuesto determinista de instrucciones) | 1.000.000 | 10.000.000 |
| Memoria | `Store` con máximo de páginas WASM (64 KiB/página) | 16 (1 MiB) | 256 (16 MiB) |
| Tiempo | epoch interruption en el worker + `WaitForExitAsync` con kill del proceso en el API | 1.000 ms | 10.000 ms |
| Red | Sin WASI ni imports de red en el guest; el worker no expone ningún puerto | — | — |
| Input | Tamaño del body HTTP en el API | 64 KiB | 256 KiB |
| Output | Lectura acotada de stdout en el API | 64 KiB | 256 KiB |
| Concurrencia | Semáforo de procesos en el API | 4 | configurable |

El worker escribe un JSON de resultado con `outputBase64`, `fuelConsumed` y `durationMs`, o un
`error` con `code`/`message`. El API traduce traps, timeouts y violaciones de límite a errores
estandarizados (`FUNCTION_TIMEOUT`, `FUNCTION_OUT_OF_FUEL`, `FUNCTION_TRAP`, etc.) sin filtrar
detalles internos.

## Metadatos (domibase_control)
Tabla `functions`:
- `id`, `project_id`, `name` (único por proyecto), `description?`
- `module bytea`, `module_sha256`
- `enabled`, `timeout_ms`, `memory_pages`, `fuel`, `max_input_bytes`, `max_output_bytes`
- `created_at`, `updated_at`

Los bytes del módulo viven en la base de control (no en el filesystem de proyecto), con límite de
5 MiB. Nunca se devuelven en respuestas; solo el hash y los límites.

## Endpoints
### Control Plane (`/api/v1`, solo propietario)
- `GET /api/v1/projects/{id}/functions`
- `POST /api/v1/projects/{id}/functions` — `{ name, description?, moduleBase64, limits? }`
- `DELETE /api/v1/projects/{id}/functions/{functionId}`

### Runtime Plane (`/functions/v1`, apikey + Bearer opcional)
- `POST /functions/v1/{name}` — body JSON arbitrario → `{ data: <resultado JSON> }`.

## SDK
`db.functions.invoke(name, payload, { signal })` → `{ data, error }`.
Separado en `functions.ts`; reutiliza el transporte JSON existente.

## Subfases
1. Worker WASM + `IFunctionExecutor` + tests unitarios del protocolo y límites.
2. Dominio, migración, repositorio, servicios y endpoints + tests de integración.
3. SDK y demo Astro `/funciones`.
4. Documentación y validación final.

## Fuera de alcance en Fase 5
- No se ejecuta código nativo del usuario, solo WASM validado.
- Sin llamadas de vuelta del guest al Runtime (sin red); el contexto se pasa en el request.
- Sin Node.js/Deno ni módulos npm en el guest.
- Sin proveedor de funciones gestionado ni colas; la invocación es síncrona.

## Estado final (2026-11-17)

**Fase 5 completada.** Implementación y verificación:

- Contrato compartido y protocolo stdin/stdout: `FunctionWorkerProtocol`, `FunctionWorkerRequest`,
  `FunctionWorkerResponse`, `FunctionOptions`, `IFunctionExecutor`, `IFunctionRepository`.
- Dominio y Application: `FunctionDefinition`, `FunctionService` (validación de nombre, base64,
  límites, hash SHA-256 y mapeo de errores).
- Worker: `backend/src/DomiBase.Functions.Worker` con `WasmFunctionRunner`. Configuración Wasmtime
  con fuel y epoch interruption; sin WASI; imports rechazados; exports obligatorios `memory` e
  `invoke` con firma `(i32, i32) -> i64`; validación del JSON de salida antes de devolverlo.
- Infraestructura: `EfFunctionRepository`, migración `20261117200708_Functions` (aplicada a
  `domibase_control`), `ProcessIsolatedFunctionExecutor` (worker one-shot, timeout con kill del
  árbol de procesos, stdout acotado, semáforo de concurrencia) y `AddDomiBaseFunctions`.
- Host: endpoints de Control y Runtime y límite de body para `/functions/v1` en `Program.cs`.
- SDK: `db.functions.invoke(name, payload?, { signal })` en `packages/client/src/functions.ts`.
- Demo: página `/funciones` en Astro, helper `invokeDemoFunction` y registro del módulo
  `demo-echo` en `scripts/dev/phase-06-demo.mjs`.
- Tests: 12 unitarios nuevos (servicio, protocolo, executor) y 11 de integración contra
  PostgreSQL real y el worker WASM. Resultados: **109 tests unitarios**, **11 tests de integración
  de Functions** y **70 tests del SDK** en verde; build Astro verificado.
- Validación final: `dotnet test backend/DomiBase.sln` → **175 tests backend** en verde (6 de
  seguridad, 109 unitarios, 60 de integración); `npm test` en `packages/client` → **70** en verde;
  `npm run build` en `examples/astro-example` → correcto. El módulo WASM incrustado en el script de
  demo se verificó invocándolo directamente con el worker (payload y contexto devueltos).

Configuración (todo con valores por defecto seguros, sin secretos):

| Variable | Default | Uso |
|---|---|---|
| `DOMIBASE_FUNCTIONS_WORKER` | `DomiBase.Functions.Worker.dll` junto al host | Ruta al worker (la factory de tests la fija al proyecto compilado). |
| `FUNCTIONS_WORKER_STARTUP_TIMEOUT_MS` | `5000` | Margen de arranque del proceso antes del timeout del guest. |
| `FUNCTIONS_MAX_MODULE_SIZE_BYTES` | `5242880` | Tamaño máximo del módulo en la base de control. |
| `FUNCTIONS_DEFAULT_FUEL` / `_MEMORY_PAGES` / `_TIMEOUT_MS` | `1000000` / `16` / `1000` | Límites por defecto al registrar una función. |
| `FUNCTIONS_MAX_INPUT_BYTES` / `_MAX_OUTPUT_BYTES` | `65536` / `65536` | Topes de payload y respuesta. |
| `FUNCTIONS_MAX_CONCURRENCY` | `4` | Máximo de workers simultáneos por proceso API. |

Nota operativa: el worker debe estar compilado y accesible desde la ruta configurada; si no existe,
el Runtime devuelve `503 FUNCTION_UNAVAILABLE` sin exponer la ruta al cliente.
