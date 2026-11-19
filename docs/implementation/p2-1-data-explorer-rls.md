# P2.1 — Explorador de filas y panel de políticas RLS

**Fecha:** 2026-11-18  
**Estado:** completado y verificado.  
**Objetivo:** añadir al panel privado dos capacidades inspiradas en el explorador de Supabase Studio —lectura de filas con RLS real y administración de políticas RLS— con contratos, diseño e implementación propios de DomiBase. Studio solo se usa como referencia visual/UX: no hay dependencias, esquemas ni endpoints de Supabase.

## 1. Alcance

### Control Plane

Nueva ruta privada, protegida por el middleware administrativo existente y **nunca** expuesta bajo `/rest/v1`:

| Método | Ruta | Acción |
| --- | --- | --- |
| `GET` | `/api/v1/projects/{projectId:guid}/data/tables/{table}/rows` | Devuelve una vista previa acotada de filas legibles por el rol funcional anónimo. |

Parámetros de consulta:

| Parámetro | Formato | Límite |
| --- | --- | --- |
| `select` | `col1,col2` | Máximo 50 columnas; por defecto todas las legibles. |
| `f` | `columna.operador.valor`, repetible | Máximo 10 filtros; operadores `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`. |
| `order` | `columna.asc[,columna2.desc]` | Máximo 3 expresiones. |
| `limit` | entero | Por defecto 50; se acota a 1–200. |
| `offset` | entero | Por defecto 0; se acota a 0–100.000. |

Respuesta:

```json
{
  "role": "anon",
  "columns": ["id", "email"],
  "rows": [{ "id": "...", "email": "..." }],
  "rowCount": 1,
  "truncated": false
}
```

`role` siempre es `"anon"`: lo informa el servidor para que el panel no pueda presentar otra identidad. `rowCount` es el número de filas devueltas en esa respuesta y `truncated` indica que el conjunto acotado puede seguir teniendo más filas.

### Panel React

Dos divisiones nuevas con pestaña y color propios:

- **Datos:** selección de tabla expuesta, columnas opcionales, hasta 10 filtros, orden, límite y desplazamiento; tabla de resultados y aviso explícito de lectura como `anon`.
- **Políticas RLS:** alta, edición, borrado y listado de políticas por tabla. El formulario ofrece solo comandos `SELECT`/`INSERT`/`UPDATE`/`DELETE`, roles `anon`/`authenticated` y las tres expresiones allowlisted por el backend: `true`, `false` y `project_id = current_setting('domibase.project_id', true)::uuid`.

El editor de SQL de solo lectura sigue siendo una capacidad separada y se ejecuta como propietario: la pestaña Datos indica expresamente que no debe usarse para verificar RLS.

## 2. Decisiones de seguridad

- **RLS real, no simulación.** El endpoint administrativo reutiliza `IRuntimeDataService.SelectAsync` y `IRuntimeProjectResolver.ResolveForProjectAsync`. La resolución por ID de proyecto usa `ApiKeyId = Guid.Empty` como centinela administrativo: exige token de propietario, no clave pública, y nunca marca `lastUsedAt`.
- **Sin escalamiento.** `DataPreviewService` llama a `SelectAsync(..., user: null)` de forma intencionada. La transacción aplica el login `_runtime`, `SET LOCAL ROLE` al rol `_anon` y las claims `request.jwt.claims`/`domibase.project_id`; no se usa superuser, owner ni `BYPASSRLS`. Ni el explorador ni el editor de políticas envían rol, base de datos, SQL ni clave al servidor.
- **Identificadores validados.** El nombre de tabla y las columnas de `select` pasan por `SchemaDdlValidator.IsIdentifier`; los filtros y ordenamientos se delegan al validador del Runtime. El panel solo construye cadenas `columna.operador.valor` a partir de opciones acotadas.
- **Políticas sin SQL libre.** El formulario usa las mismas listas que valida `SchemaRequestParser`/`SchemaDdlValidator`. Cualquier expresión fuera de la allowlist se rechaza antes de abrir una conexión privilegiada. Los nombres de política se validan localmente y otra vez en el backend.
- **Auditoría.** Cada vista previa registra `data.preview` con `TargetType = "table"`, `TargetId = table`, `ProjectId`, base, rol `anon`, filas, truncamiento, límites y número de filtros. No se registra SQL, ni filtros literales, ni secretos. Las mutaciones de políticas reutilizan `schema.policy.*` de P1.1.
- **Sin enseñanza del Runtime.** La ruta vive bajo `/api/v1`; `X-DomiBase-Admin-Token` sigue sin tener efecto en `/rest/v1` y ninguna clave pública puede consultar la vista previa ni las políticas.

## 3. Archivos creados

**Backend — Application**
- `backend/src/DomiBase.Application/Data/DataPreviewService.cs` — validación, resolución administrativa, ejecución como `anon` y auditoría.

**Backend — API**
- `backend/src/DomiBase.Api/Endpoints/DataPreviewControlEndpoints.cs` — endpoint `GET .../data/tables/{table}/rows`.

**Backend — Tests**
- `backend/tests/DomiBase.IntegrationTests/DataPreviewTests.cs` — 5 pruebas contra PostgreSQL real.

**Frontend**
- `frontend/admin/src/components/DataSection.tsx` — explorador de filas.
- `frontend/admin/src/components/PoliciesSection.tsx` — editor de políticas RLS.
- `docs/implementation/p2-1-data-explorer-rls.md`.

## 4. Archivos modificados

- `backend/src/DomiBase.Application/Runtime/RuntimeServiceContracts.cs` — contrato `ResolveForProjectAsync(Guid, CancellationToken)`.
- `backend/src/DomiBase.Infrastructure/Runtime/RuntimeProjectResolver.cs` — resolución por ID de proyecto con centinela administrativo.
- `backend/src/DomiBase.Api/Program.cs` — mapeo de `MapDataPreviewControlEndpoints()` y registro/inyección del servicio.
- `backend/src/DomiBase.Api/ControlPlane/ControlPlaneServiceCollectionExtensions.cs` — registro de `DataPreviewService`.
- `backend/tests/DomiBase.SecurityTests/ControlPlaneAuthenticationTests.cs` — 3 pruebas de autenticación de la nueva ruta.
- `frontend/admin/src/lib/control-client.ts` — tipos `DataPreviewRequest`, `DataPreviewResult`, `RlsPolicy`, `PolicyRequest` y `SchemaMutationResult`; métodos `previewTableRows`, `listPolicies`, `createPolicy`, `updatePolicy` y `deletePolicy`.
- `frontend/admin/src/lib/control-client.test.ts` — 4 pruebas de rutas, query params, errores y `AbortSignal`.
- `frontend/admin/src/App.tsx` — pestañas y paneles `data`/`policies`.
- `frontend/admin/src/components/TabRail.tsx` — ampliación de `SectionId`.
- `frontend/admin/src/styles.css` — tableros `#176b6f` (Datos) y `#8a2f3b` (Políticas), filtros, tabla de resultados, tarjetas de políticas y adaptación móvil.
- Documentación vigente: `README.md`, `docs/architecture/overview.md`, `docs/architecture/security.md`, `docs/architecture/rest-contract.md`, `docs/implementation/roadmap.md`, `docs/implementation/admin-panel-plan.md`, `docs/implementation/schema-inspection.md`, `docs/implementation/technical-audit.md`, `docs/audits/DOMIBASE_TECHNICAL_AUDIT.md`, `docs/development/local-setup.md`, `PRODUCT.md`, `frontend/admin/README.md` y `.impeccable/surfaces/frontend-admin.md`.

## 5. Migraciones creadas

**Ninguna.** No hay esquema nuevo en `domibase_control`; la auditoría reutiliza `audit_entries` y las políticas se administran en cada base de proyecto con el DDL validado de P1.1.

## 6. Pruebas ejecutadas

```powershell
dotnet test backend/DomiBase.sln
cd frontend/admin
npm.cmd test
npm.cmd run build
```

| Suite | Antes de P2.1 | Después de P2.1 |
| --- | ---: | ---: |
| UnitTests | 288 | **288** |
| IntegrationTests | 129 | **134** |
| SecurityTests | 41 | **44** |
| **Total backend** | **458** | **466** |
| Panel (Vitest) | 45 | **49** |

Casos nuevos relevantes:

- Vista previa con política SELECT y filtro parametrizado; `role = "anon"`.
- Tabla con RLS pero sin política SELECT → 0 filas.
- Política con claim `domibase.project_id` → solo filas propias del proyecto.
- Tabla sin RLS, sin GRANT e identificadores inyectados en tabla/columna → `404`/`400` sin dañar la tabla señuelo.
- Aislamiento de vistas previas entre dos proyectos reales.
- Nueva ruta sin token, con token erróneo y con solo `apikey` público → `401`.
- Cliente: parámetros allowlisted, omisión de opciones vacías, error canónico y rutas de políticas con `AbortSignal`.
- Build del panel: `tsc --noEmit` y Vite sin errores (37 módulos).

## 7. Verificación E2E real

Se usó Chrome headless contra la API en `127.0.0.1:5080` y el build servido en `127.0.0.1:5191` (el puerto `5190` ya estaba ocupado por un Vite ajeno y no se tocó):

1. Proyecto `gg-wp`, tabla temporal `e2e_ui_<sufijo>` creada desde la Control API con columnas `id`/`nombre`.
2. Desde el panel: crear política SELECT para `anon`, confirmar que aparece en la lista y leer filas en la pestaña Datos.
3. Resultado: `E2E OK · gg-wp · e2e_ui_mwlqpktu`; preview `anon 0 filas · tabla e2e_ui_mwlqpktu`.
4. Diseño comprobado a 1440, 900 y 390 px sin desbordamiento de página; color de división Datos `#176b6f`.
5. La misma sesión borró la política y la tabla temporal. Verificación posterior: `gg-wp tables after E2E: 0`.
6. Sin errores de consola del navegador. Capturas: `domibase-policies.png`, `domibase-data.png` y `domibase-data-mobile.png` en el directorio temporal de la sesión.

El primer intento contra `paneles-solares` mostró `PostgreSQL state 42704`: ese proyecto es legacy y no tiene el rol funcional `authenticated`, mientras que el DDL de políticas revoca privilegios de ambos roles funcionales. No es un fallo de la interfaz; la E2E definitiva usó `gg-wp`, con roles completos. Proyectos anteriores a P1.1 pueden necesitar reprovisionado o la creación del rol `authenticated` antes de usar el editor de políticas.

## 8. Resultados

- Build backend con **0 errores y 0 warnings**.
- Suite backend: **466/466 tests en verde**; panel: **49/49 tests Vitest**; build de producción correcto.
- PostgreSQL 18 en Docker (`127.0.0.1:5433`) sigue healthy; no se detuvieron contenedores ni se tocó el homelab.
- La vista previa respeta GRANT y RLS reales: un visitante sin política SELECT y otro proyecto no pueden ver esas filas.
- No se construyó ni publicó la imagen Docker de la API; no se hicieron commits ni pushes.

## 9. Riesgos y limitaciones pendientes

- La vista previa es de solo lectura; no sustituye al editor SQL ni permite insertar datos de prueba desde la pestaña Datos.
- `truncated` se calcula con `rowCount >= limit`; no hay `COUNT(*)` ni paginación de vuelta.
- El editor de políticas no admite expresiones SQL arbitrarias. Las necesidades fuera de la allowlist deben resolverse desde la Control API o una migración versionada.
- Los proyectos creados antes de que existiera el rol `authenticated` pueden rechazar mutaciones de políticas con `42704`.
- No se añadió exportación CSV ni persistencia de filtros en la URL.

## 10. Siguiente fase

Las próximas entregas P2 se planificarán individualmente. Ninguna se marca terminada sin build, pruebas, documentación y verificación de sus criterios de aceptación.
