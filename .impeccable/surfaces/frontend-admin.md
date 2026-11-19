---
version: 2
slug: "frontend-admin"
primary_target: "frontend/admin"
related_targets: []
---

Scope: panel privado de administración (`frontend/admin`), modo Operate. Audiencia: el único propietario de DomiBase. Tarea: operar proyectos, provisioning, claves, buckets, funciones, esquema, datos, políticas RLS, migraciones, extensiones, backups y auditoría desde la Control API. Prueba: estados reales del sistema y acciones que se ejecutan contra `/api/v1`. Restricciones: token solo en sessionStorage, sin secretos en bundle, proxy de mismo origen, sin SQL libre, roles físicos ni bases de datos enviados por el cliente.

## Direction contract

THESIS: El panel es una consola de producto, no un manual encuadernado. Se niega a la vez al dashboard de KPIs decorativos y al objeto editorial: cada pantalla es una superficie de trabajo con navegación persistente, densidad alta y estado real a la vista. La categoría siempre entrega tarjetas iguales con iconos; aquí la tabla y el panel de control son el contenido.

OWN-WORLD: Barra lateral oscura fija con navegación compacta y acento verde; lienzo claro y neutral con paneles blancos de borde 1 px y sombra suave; tipografía system UI para interfaz y mono tabular para identificadores, comandos y fechas; botones de 5 px de radio, chips de estado con punto y texto, tablas densas con cabecera sticky y scroll contenido; verde reservado para acción principal y selección, nunca para decorar.

STORY: El propietario entra, reconoce de inmediato la división activa en el raíl, ve el estado real del proyecto y actúa sin traducir metáforas. Crear, revocar, leer como `anon` o ajustar una política permitida son gestos visibles y consecuentes. La ausencia de secretos y de SQL libre es parte de la interfaz, no una nota al pie.

FIRST VIEWPORT: Raíl izquierdo de 15–16 rem con marca, doce destinos y cierre de sesión; cabecera sticky clara con el título de la división y el selector de proyecto activo a la derecha; debajo, un panel único con la tabla de proyectos (slug, nombre, estado, base, cupo, creado, acciones) y “Nuevo proyecto” como botón verde en la cabecera de herramientas. Sin kickers, métricas ni hojas translúcidas: el primer vistazo demuestra operación real.

FORM: Consola Studio-like fijada por el propietario (referencia de categoría, sin dependencias, contratos ni código de Supabase).

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
