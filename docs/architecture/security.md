# Seguridad de DomiBase

## Límites de confianza
1. Panel privado y Control API.
2. Runtime público.
3. Base de control.
4. Base de datos de cada proyecto.
5. Aplicaciones consumidoras.

## API keys
`db_public_` identifica el proyecto y puede aparecer en el frontend. No concede permisos por sí sola. Guardar hashes, mostrar el valor completo solo una vez y permitir revocación/rotación. `db_secret_` queda reservado para operaciones server-side futuras.

## PostgreSQL
- Una base independiente por proyecto.
- Usuario de migraciones/propietario separado del usuario Runtime.
- Runtime sin SUPERUSER, CREATEDB, CREATEROLE ni BYPASSRLS.
- GRANT mínimos y RLS habilitado en tablas expuestas.
- Roles `domibase_anon` y `domibase_authenticated` donde corresponda.
- Contexto de claims mediante configuración local a la transacción.
- Nunca confiar en roles o project IDs enviados por el cliente.

## SQL seguro
Valores siempre parametrizados. Identificadores de tablas/columnas validados contra metadatos/allowlist y quoted correctamente. Nunca aceptar fragmentos SQL arbitrarios desde el SDK.

## Runtime
- Rate limiting por proyecto y dirección IP.
- Límites de tamaño de body y cantidad de filas.
- CORS por proyecto como control complementario, no como autorización.
- Errores sin stack traces ni secretos.
- Logs estructurados con tokens, contraseñas y API keys redactados.

## Control API
Autenticación administrativa independiente. Autorizar cada operación. No exponer la base de control ni sus credenciales a Runtime público.

## Pruebas de seguridad
Probar claves inválidas/revocadas, aislamiento entre proyectos, RLS, SQL injection, tablas internas, escalamiento de roles y contaminación del contexto en conexiones pooled.
