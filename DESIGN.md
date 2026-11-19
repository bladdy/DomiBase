---
name: DomiBase Admin
description: Consola operativa del Control Plane privado de DomiBase, con lenguaje de Studio.
colors:
  canvas: "#f5f7f6"
  panel: "#ffffff"
  panel-subtle: "#f1f4f3"
  text: "#1c2524"
  text-muted: "#5d6b68"
  border: "#e1e7e5"
  border-strong: "#c6d0cd"
  rail: "#171c1c"
  rail-hover: "#232b2a"
  rail-active: "#2a3533"
  rail-text: "#edf3f1"
  rail-muted: "#9aa8a5"
  accent: "#2bb87a"
  accent-strong: "#148056"
  accent-soft: "#e5f5ed"
  danger: "#a83228"
  danger-soft: "#fdece9"
  warning: "#835600"
  warning-soft: "#fff5d9"
typography:
  body:
    fontFamily: "ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.5
  title:
    fontFamily: "ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "1.05rem"
    fontWeight: 650
    lineHeight: 1.3
    letterSpacing: "-0.012em"
  section:
    fontFamily: "ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "1.08rem"
    fontWeight: 650
    lineHeight: 1.3
    letterSpacing: "-0.012em"
  mono:
    fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"
    fontSize: "0.8rem"
    fontVariantNumeric: "tabular-nums"
shapes:
  radius-sm: "4px"
  radius: "6px"
  radius-lg: "9px"
  shadow-panel: "0 1px 2px rgba(16,24,23,.06), 0 10px 28px rgba(16,24,23,.05)"
  shadow-dialog: "0 18px 50px rgba(8,14,13,.24)"
spacing:
  1: "0.25rem"
  2: "0.5rem"
  3: "0.75rem"
  4: "1rem"
  5: "1.5rem"
  6: "2rem"
motion:
  panel-in: "160ms cubic-bezier(.2,.8,.2,1)"
  control: "150ms ease"
---

# DomiBase Admin — visual world

## Thesis

La consola es una superficie de trabajo, no un documento editorial ni un dashboard de KPIs. La inspiración de categoría es Supabase Studio: raíl oscuro persistente, lienzo neutro, densidad de tabla y verde funcional. Es solo inspiración visual y de UX; DomiBase no usa dependencias, contratos, SDK, GoTrue ni PostgREST de Supabase.

## Shell

- Raíl lateral de 15.5 rem en modo escritorio, fijo y oscuro. Contiene marca, doce destinos de navegación compactos, estado de sesión y cierre de sesión.
- El destino activo se marca con fondo más claro, texto completo y una barra verde de 2 px al inicio. El color nunca es el único indicador: el texto activo también cambia de peso y tono.
- Cabecera clara sticky con el título de la división y el selector de proyecto activo alineado a la derecha cuando corresponde.
- Contenido en un único panel por división: borde de 1 px, radio de 9 px y sombra suave con desplazamiento.

## Color

- Superficies claras y neutras: lienzo `#f5f7f6`, panel `#ffffff`, subtareas `#f1f4f3`.
- Texto principal `#1c2524`, texto secundario `#5d6b68`, bordes `#e1e7e5`.
- El verde `#148056` para acción principal, selección y foco. El acento más claro `#2bb87a` para marcas y barras activas. No se usa para decorar tarjetas.
- Rojo y ámbar quedan reservados a error, advertencia y estados de provisioning. Cada estado incluye texto, no solo color.
- Tema oscuro automático con el mismo sistema de roles: superficie de trabajo `#151c1c`, raíl `#0a0f0f`, texto `#e7efec` y acento `#3ecf8e`.

## Typography

- Una sola familia system UI para toda la interfaz. Sin fuentes display.
- Cuerpo 14 px para densidad de consola, títulos de división 17–18 px con peso 650 y tracking -0.012em.
- Monoespacio tabular para ids, slug, fechas, tokens, comandos SQL y contadores.

## Components

- Botones de 4 px de radio, 34 px de alto, primario verde, secundario con borde y destructivo sobre fondo rojo claro.
- Campos de 4 px de radio, borde visible de 1 px y foco verde con anillo suave.
- Tablas densas de cabecera sticky, filas de ~38 px, scroll contenido y `min-inline-size` para preservar columnas. El contenedor nunca recorta el scroll de la página.
- Chips de estado con punto y texto legible. `Ready`, `Failed`, `Provisioning`, `Deleting` mantienen su significado sin depender del color.
- Formularios agrupados en paneles de fondo sutil, con títulos de campo cortos y texto de ayuda monotipo cuando describe contratos técnicos.
- Diálogos modales con tarjeta blanca, fondo oscurecido y acciones alineadas a la derecha.

## Responsive behavior

- 1440 px: raíl completo, cabecera de una línea, tablas desplazables dentro del panel.
- 900 px: raíl se convierte en barra horizontal de navegación; el selector de proyecto baja a una segunda línea cuando hace falta. Se eliminan artefactos de escritorio como el texto de sesión.
- 390 px: campos y botones a ancho completo, grupos de filtros en una columna, tablas siguen desplazándose dentro de su contenedor sin provocar scroll horizontal en la página. El login ocupa una tarjeta centrada.

## Accessibility and motion

- Objetivos táctiles de 44 px en punteros gruesos.
- Foco visible de 2 px con offset de 2 px en todos los controles.
- `prefers-reduced-motion` elimina la animación de entrada y las transiciones.
- `prefers-contrast: more` refuerza bordes y textos secundarios; `forced-colors` conserva bordes y distinciones de estado.
- Respeta la escala de texto del sistema; no hay `user-scalable=no` ni tamaños fijos que bloqueen zoom.
