# Auditoría técnica de DomiBase — punto de entrada

**Fecha:** 2026-11-17  
**Finalidad:** satisfacer la ruta normalizada `docs/audits/DOMIBASE_TECHNICAL_AUDIT.md` exigida por la misión sin duplicar el informe real.

## Documento de referencia

La auditoría técnica integral es `docs/implementation/technical-audit.md` (fecha 2026-11-17, alcance solo lectura, clasificación ✅/◐/⛔/📄). Ese documento es la fuente de verdad para el inventario de código, dependencias, pruebas y capacidades. Este archivo solo indica dónde está y qué hallazgos se han resuelto desde que se emitió. Los siete hallazgos de consistencia de su sección 9 quedaron cerrados el 2026-11-18 en la subsección «Resolución de los hallazgos de consistencia»; las clasificaciones ⛔/◐ originales se conservan como registro histórico.

## Hallazgos resueltos desde la auditoría base

La auditoría base se emitió sobre el estado previo a las entregas P0, P1.1, P1.2, P1.3, P1.5, P1.4, P1.6 y P1.7. Su tabla de consistencia documental (sección 9) ya no describe el estado actual:

| Hallazgo de la auditoría | Resolución |
| --- | --- |
| Gestión de proyectos incompleta (borrado/configuración) | P0.1: `DELETE /api/v1/projects/{id}` con confirmación doble, revocación de claves, limpieza idempotente y auditoría. `docs/implementation/p0-1-project-deletion.md`. |
| Provisioning sin trazabilidad ni compensación | P0.2: pasos `Running`/terminales, `Attempt`, `CorrelationId`, compensación real y reintento. `docs/implementation/p0-2-provisioning.md`. |
| Rotación y expiración de claves ausentes | P0.3: rotación con solapamiento de 0–1440 minutos y expiración server-side. `docs/implementation/p0-3-api-keys.md`. |
| Secretos sin keyring rotatable | P0.4: `DOMIBASE_API_KEY_PEPPERS` y `DOMIBASE_SECRETS_KEYS`, compatibilidad `v1` y procedimiento de backup. `docs/implementation/p0-4-secrets.md`. |
| DDL administrativo no implementado | P1.1: DDL tipado y validado para tablas, columnas, índices y políticas, sin SQL arbitrario. `docs/implementation/p1-1-schema-admin.md`. |
| Migraciones versionadas ausentes | P1.3: historial inmutable por proyecto con checksum determinista, orden creciente, idempotencia y reutilización del DDL validado. `docs/implementation/p1-3-migrations.md`. |
| SQL Editor administrativo ausente | P1.2: consulta acotada de solo lectura con validador léxico, transacción `READ ONLY`, row cap, timeout y auditoría sin SQL. `docs/implementation/p1-2-sql-editor.md`. |
| Catálogo de extensiones ausente | P1.5: allowlist de diez extensiones `trusted`, catálogo en vivo vía `READ ONLY`, DDL tipado e idempotente como owner, auditoría sin SQL y pestaña en el panel. `docs/implementation/p1-5-extensions.md`. |
| Rate limit solo por IP | P1.4: cuota por proyecto con override administrativo, partición compartida por las claves del proyecto y fallback por IP para claves inválidas; columna **Cupo** en el panel. `docs/implementation/p1-4-rate-limits.md`. |
| Backups/restauración ausentes | P1.6: volcados lógicos `pg_dump --format=custom` por proyecto, SHA-256 verificado, restauración transaccional con confirmación por `slug`, descarga y borrado desde la pestaña **Backups**. `docs/implementation/p1-6-backups.md`. |
| Trazabilidad administrativa sin lectura desde el panel | P1.7: consulta acotada de `audit_entries` y `provisioning_operations` desde `/api/v1`, con redacción de secretos, filtros validados y pestaña **Auditoría**. `docs/implementation/p1-7-audit-viewer.md`. |
| `README.md` con cifras y superficie desactualizadas | Actualizado a 466 tests backend, 49 del panel y al estado P1.2/P1.3/P1.4/P1.5/P1.6/P1.7/P2.1. |
| Roadmap con P0 “en curso” y sin entregas P1 | P0 marcado como completado y P1.1/P1.2/P1.3/P1.4/P1.5/P1.6/P1.7/P2.1 registrados con su evidencia. |
| `docs/architecture/overview.md` con 213 tests y sin contrato DDL | Actualizado a 466 tests, contratos DDL, migraciones, SQL, extensiones, cuotas por proyecto, backups, auditoría y explorador RLS, y estado P1.2–P2.1. |
| `docs/architecture/security.md` sin amenazas de DDL ni recuento de seguridad actual | Añadidas las secciones de DDL, migraciones, SQL Editor, extensiones, rate limit por proyecto, backups, auditoría y vista previa RLS, y el total de 44 security tests. |

## Verificación

Ejecutada el 2026-11-17 contra PostgreSQL 18.7 en Docker (`127.0.0.1:5433`):

```powershell
dotnet build backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
dotnet test  backend/DomiBase.sln --v:q -p:BaseOutputPath=bin-p11\
```

Resultado: build con **0 errores y 0 warnings** y **383/383 tests en verde** (244 unitarios, 116 de integración y 23 de seguridad). El panel añade **35/35 tests Vitest** y build de producción correcto (`npm.cmd test`, `npm.cmd run build`). La pestaña de migraciones superó la verificación E2E real con Chrome headless: **15/15 comprobaciones** (`node scripts/dev/verify-migrations-panel.mjs`), con capturas en `.impeccable/review/migrations-*.png`. La edición de cupos cerró **14/14 comprobaciones** (`node scripts/dev/verify-rate-limits-panel.mjs`), con capturas en `.impeccable/review/rate-limits-*.png`.

El sufijo `bin-p11\` se usó únicamente para no interrumpir una instancia manual de la API que mantenía bloqueados los DLL de `bin/Debug/net10.0`. No se construyó ni publicó la imagen Docker, no se hicieron commits ni pushes y no se modificó el homelab.

**Actualización P1.6 (2026-11-18):** build con 0 errores/0 warnings y **427/427 tests backend** (272 unitarios, 120 integración, 35 seguridad), **42/42 tests Vitest** y build de producción correcto. La verificación E2E real de backups (`node scripts/dev/verify-backups-panel.mjs`) cerró **18/18 comprobaciones**: creación, descarga, mutación, restauración confirmada por `slug` con recuperación efectiva de la fila original, borrado, 390×844 sin desbordamiento y consola/respuestas limpias; capturas en `.impeccable/review/backups-*.png`. Sigue sin construirse ni publicarse la imagen de la API y sin tocarse el homelab.

**Actualización P1.7 (2026-11-18):** build con 0 errores/0 warnings y **458/458 tests backend** (288 unitarios, 129 integración, 41 seguridad), **45/45 tests Vitest** y build de producción correcto. La verificación E2E real del visor de auditoría (`node scripts/dev/verify-audit-panel.mjs`) cerró **16/16 comprobaciones**: 21 claves reales para forzar paginación, redacción del servidor verificada en `api_key.create`, filtros y paginación desde el panel, pasos de provisioning del proyecto activo, 390×844 sin desbordamiento y consola/respuestas limpias; capturas en `.impeccable/review/audit-*.png`. La migración `20261118045153_AddAuditProjectId` se aplicó solo a `domibase_control`. No se construyó ni publicó la imagen de la API y no se tocó el homelab.

**Actualización P2.1 (2026-11-18):** build con 0 errores/0 warnings y **466/466 tests backend** (288 unitarios, 134 integración, 44 seguridad), **49/49 tests Vitest** y build de producción correcto. La verificación E2E real del explorador y las políticas (`gg-wp`, Chrome headless, API en 5080 y build en 5191) creó una tabla y una política temporal, consultó filas como `anon`, comprobó el diseño a 1440/900/390 px sin overflow y eliminó ambos objetos; quedaron cero tablas en el proyecto de prueba. La nueva ruta `/api/v1/projects/{id}/data/tables/{table}/rows` no existe bajo `/rest/v1`, no acepta rol/base/SQL/clave y audita `data.preview`. No se construyó ni publicó la imagen de la API y no se tocó el homelab.

**Actualización alcance mínimo (2026-11-18):** el panel se simplificó a `Login → Proyectos → PostgreSQL → Tablas/campos/registros → Conexión → SDK`; Auth de usuarios finales, Storage, Realtime, Functions, SQL Editor, Migraciones, Extensiones, Backups y Auditoría quedaron fuera de la navegación y del SDK soportado, conservando su código backend/shared como registro histórico. Estado vigente: **491/491 tests backend** (311 unitarios, 134 integración, 46 seguridad), **70/70 tests del SDK** y **57/57 tests del panel**, con E2E del flujo mínimo 14/14 y 15/15 tras reiniciar PostgreSQL y CRUD de Astro verificado. Fuentes vigentes: `docs/implementation/admin-minimal-scope.md` y `docs/implementation/admin-minimal-e2e.md`. No se construyó ni publicó imagen, no se hicieron commits/pushes y no se modificó el homelab.

## Alcance

Este archivo no sustituye a la auditoría base: no reevalúa capacidades, no cambia las clasificaciones ✅/◐/⛔/📄 y no añade comparaciones con Supabase. Para cualquier afirmación sobre el estado del producto, usar `docs/implementation/technical-audit.md` junto con los informes de fase enlazados.
