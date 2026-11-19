# Realtime — subfase 3.3: ejemplo Astro

Tercera subfase de la Fase 3 de producto.

## Entregado

### Página `/tiempo-real`
- Ubicación: `examples/astro-example/src/pages/tiempo-real.astro`.
- Server-rendered; inyecta `apiUrl` y `projectKey` al cliente mediante `define:vars`.
- Script client-side que:
  - Crea el SDK con `createClient`.
  - Se suscribe a `contactos` y `notas`.
  - Muestra cada evento en un log con hora, operación, tabla y `rowId`.
  - Permite limpiar el log.

### Navegación
- `Layout.astro` añade enlace "Tiempo real" en la navegación principal.

### Nota sobre la API key pública
La API key pública se expone intencionadamente en el navegador porque solo identifica el proyecto. Los permisos efectivos y el filtrado de datos siguen resolviéndose server-side mediante GRANT/RLS y el Bearer del usuario autenticado.

## Verificación

Para probar la página:

```powershell
node scripts/dev/phase-06-demo.mjs
cd examples/astro-example
cmd /c npm install
cmd /c npm run dev
```

Abre http://localhost:4325/tiempo-real y, en otra pestaña, crea/edita contactos o notas. Los eventos aparecerán en el log.
