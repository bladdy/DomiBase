# P2.2 — Rediseño visual del panel admin

## Objetivo

Sustituir el mundo visual anterior del panel (`manual de operaciones encuadernado`) por una consola operativa de categoría Studio, porque el propietario no estaba conforme con el diseño de DomiBase. El cambio es exclusivamente visual: colores, tipografía, espaciado, navegación y componentes.

## Alcance

### Incluido

- `frontend/admin/src/styles.css`: sistema visual completo nuevo.
- `frontend/admin/src/App.tsx`, `TabRail.tsx`, `LoginGate.tsx`, `ProjectsSection.tsx`, `DataSection.tsx`, `PoliciesSection.tsx`: texto y clases visuales.
- `frontend/admin/index.html`: `color-scheme`, `text-scale` y favicon.
- `DESIGN.md` y `.impeccable/surfaces/frontend-admin.md`: nueva dirección visual.
- `PRODUCT.md`: referencia de categoría aprobada como inspiración visual/UX.
- `frontend/admin/README.md` y `docs/implementation/roadmap.md`: documentación de la entrega.
- `.impeccable/review/redesign-*.png`: capturas de evidencia.

### Excluido

- Contratos HTTP, endpoints, modelos, estados o códigos de error.
- Token administrativo, `sessionStorage`, CSP, proxy `/api` ni redacción de secretos.
- RLS, provisioning, claves, migraciones, backups ni auditoría.
- Dependencias de Supabase, SDK, GoTrue ni PostgREST.
- Imagen Docker, homelab ni stacks existentes.

## Dirección visual

| Antes | Ahora |
| --- | --- |
| Manual editorial con tableros por división y hojas translúcidas | Consola con raíl oscuro persistente y lienzo neutro |
| Pestañas anchas, tipografía grande y mucha separación | Navegación compacta de 12 destinos, tablas densas y 14 px de cuerpo |
| Colores saturados por módulo | Verde único para acción, selección y foco; rojo/ámbar solo para estado |
| Sin modo oscuro | Tema oscuro automático con las mismas roles semánticos |
| Componentes irrepetibles por sección | Botones, campos, chips, tablas y diálogos consistentes |

La inspiración es Supabase Studio como lenguaje de consola. No hay dependencias, contratos ni código de Supabase.

## Decisiones concretas

- Raíl lateral fijo de 15.5 rem en escritorio; a 900 px pasa a barra horizontal con scroll propio, sin provocar scroll en el documento.
- Panel único por división con borde de 1 px, radio de 9 px y sombra con desplazamiento.
- Cabecera sticky con título y selector de proyecto activo.
- Una familia system UI; monoespacio tabular para ids, slugs, fechas, tokens y SQL.
- Objetivos táctiles de 44 px con puntero grueso y foco visible de 2 px.
- `prefers-reduced-motion`, `prefers-contrast: more` y `forced-colors` contemplados.
- Contraste verificado en los pares principales: texto/panel 15.68:1, texto secundario/panel 5.57:1, acento/panel 4.94:1, texto del raíl/fondo 15.33:1, texto secundario del raíl/fondo 6.99:1.

## Verificación

- `cmd /c npm test`: **49/49 tests** del panel en verde.
- `cmd /c npm run build`: typecheck y build de producción correctos; CSP inyectada.
- `git diff --check`: limpio.
- Render real con Chrome headless sobre el servidor de desarrollo y la API local:
  - 12 secciones × 3 breakpoints (1440×900, 900×900 y 390×844).
  - **0** documentos con `scrollWidth > clientWidth`.
  - **0** errores de consola o excepciones de página.
  - El raíl mantiene su ancho en escritorio y pasa a barra horizontal en tablet/móvil.
  - Las tablas conservan scroll interno sin desbordar la página.
- Capturas en `.impeccable/review/redesign-{desktop,tablet,mobile}-{login,projects,data,policies}.png`.

## Estado

Implementado y verificado técnicamente. No se realizaron cambios funcionales ni de seguridad. La revisión estética final queda a criterio del propietario con las capturas adjuntas.
